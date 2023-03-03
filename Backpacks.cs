﻿// #define DEBUG_POOLING
// #define DEBUG_BACKPACK_LIFECYCLE

using Facepunch;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Network;
using Newtonsoft.Json.Converters;
using Oxide.Core.Configuration;
using Oxide.Core.Libraries;
using Rust;
using UnityEngine;
using UnityEngine.UI;
using Time = UnityEngine.Time;

namespace Oxide.Plugins
{
    [Info("Backpacks", "WhiteThunder", "3.11.0")]
    [Description("Allows players to have a Backpack which provides them extra inventory space.")]
    internal class Backpacks : CovalencePlugin
    {
        #region Fields

        private static int _maxCapacityPerPage = 48;

        private const int MinRows = 1;
        private const int MaxRows = 8;
        private const int MinCapacity = 1;
        private const int MaxCapacity = 48;
        private const int SlotsPerRow = 6;
        private const int ReclaimEntryMaxSize = 40;
        private const float StandardLootDelay = 0.1f;
        private const Item.Flag UnsearchableItemFlag = (Item.Flag)(1 << 24);

        private const string UsagePermission = "backpacks.use";
        private const string SizePermission = "backpacks.size";
        private const string GUIPermission = "backpacks.gui";
        private const string FetchPermission = "backpacks.fetch";
        private const string GatherPermission = "backpacks.gather";
        private const string RetrievePermission = "backpacks.retrieve";
        private const string AdminPermission = "backpacks.admin";
        private const string KeepOnDeathPermission = "backpacks.keepondeath";
        private const string LegacyKeepOnWipePermission = "backpacks.keeponwipe";
        private const string LegacyNoBlacklistPermission = "backpacks.noblacklist";

        private const string CoffinPrefab = "assets/prefabs/misc/halloween/coffin/coffinstorage.prefab";
        private const string DroppedBackpackPrefab = "assets/prefabs/misc/item drop/item_drop_backpack.prefab";
        private const string ResizableLootPanelName = "generic_resizable";

        private const int SaddleBagItemId = 1400460850;

        private readonly BackpackCapacityManager _backpackCapacityManager;
        private readonly BackpackManager _backpackManager;
        private readonly SubscriberManager _subscriberManager = new SubscriberManager();

        private ProtectionProperties _immortalProtection;
        private Effect _reusableEffect = new Effect();
        private string _cachedButtonUi;

        private readonly ApiInstance _api;
        private Configuration _config;
        private StoredData _storedData;
        private readonly HashSet<ulong> _uiViewers = new HashSet<ulong>();
        private Coroutine _saveRoutine;

        [PluginReference]
        private readonly Plugin Arena, BagOfHolding, EventManager, ItemRetriever;

        public Backpacks()
        {
            _backpackCapacityManager = new BackpackCapacityManager(this);
            _backpackManager = new BackpackManager(this);
            _api = new ApiInstance(this);
        }

        #endregion

        #region Hooks

        private void Init()
        {
            permission.RegisterPermission(UsagePermission, this);
            permission.RegisterPermission(GUIPermission, this);
            permission.RegisterPermission(FetchPermission, this);
            permission.RegisterPermission(GatherPermission, this);
            permission.RegisterPermission(RetrievePermission, this);
            permission.RegisterPermission(AdminPermission, this);
            permission.RegisterPermission(KeepOnDeathPermission, this);

            _config.Init(this);

            _maxCapacityPerPage = Mathf.Clamp(_config.BackpackSize.MaxCapacityPerPage, MinCapacity, MaxCapacity);

            _backpackCapacityManager.Init(_config);

            PoolUtils.ResizePools();

            _storedData = StoredData.Load();

            Unsubscribe(nameof(OnPlayerSleep));
            Unsubscribe(nameof(OnPlayerSleepEnded));

            if (_config.GUI.Enabled)
            {
                AddCovalenceCommand("backpackgui", nameof(ToggleBackpackGUICommand));
            }
            else
            {
                Unsubscribe(nameof(OnPlayerConnected));
                Unsubscribe(nameof(OnNpcConversationStart));
                Unsubscribe(nameof(OnNpcConversationEnded));
            }
        }

        private void OnServerInitialized()
        {
            _immortalProtection = ScriptableObject.CreateInstance<ProtectionProperties>();
            _immortalProtection.name = "BackpacksProtection";
            _immortalProtection.Add(1);

            RegisterAsItemSupplier();

            if (_config.GUI.Enabled)
            {
                Subscribe(nameof(OnPlayerSleep));
                Subscribe(nameof(OnPlayerSleepEnded));
                Subscribe(nameof(OnPlayerConnected));
                Subscribe(nameof(OnNpcConversationStart));
                Subscribe(nameof(OnNpcConversationEnded));

                foreach (var player in BasePlayer.activePlayerList)
                {
                    MaybeCreateButtonUi(player);
                }
            }
        }

        private void Unload()
        {
            UnityEngine.Object.Destroy(_immortalProtection);

            RestartSaveRoutine(async: false, keepInUseBackpacks: false);

            BackpackNetworkController.ResetNetworkGroupId();

            foreach (var player in BasePlayer.activePlayerList)
            {
                DestroyButtonUi(player);
            }

            PoolUtils.ResizePools(empty: true);
        }

        private void OnNewSave(string filename)
        {
            if (!_config.ClearOnWipe.Enabled)
                return;

            _backpackManager.ClearCache();

            IEnumerable<string> fileNames;
            try
            {
                fileNames = Interface.Oxide.DataFileSystem.GetFiles(Name)
                    .Select(fn => {
                        return fn.Split(Path.DirectorySeparatorChar).Last()
                            .Replace(".json", string.Empty);
                    });
            }
            catch (DirectoryNotFoundException)
            {
                // No backpacks to clear.
                return;
            }

            var skippedBackpackCount = 0;

            foreach (var fileName in fileNames)
            {
                ulong userId;
                if (!ulong.TryParse(fileName, out userId))
                    continue;

                var ruleset = _config.ClearOnWipe.GetForPlayer(fileName);
                if (ruleset == null || ruleset.DisallowsAll)
                {
                    _backpackManager.ClearBackpackFile(userId);
                    continue;
                }

                if (ruleset.AllowsAll)
                    continue;

                var backpack = _backpackManager.GetBackpackIfExists(userId);
                if (backpack == null)
                    continue;

                backpack.EraseContents(ruleset);
                backpack.SaveIfChanged();
            }

            _backpackManager.ClearCache();
            LogWarning($"New save created. Backpacks were wiped according to the config and player permissions.");
        }

        private void OnServerSave()
        {
            RestartSaveRoutine(async: true, keepInUseBackpacks: true);
        }

        private void OnPluginLoaded(Plugin plugin)
        {
            switch (plugin.Name)
            {
                case nameof(ItemRetriever):
                    RegisterAsItemSupplier();
                    break;
                case nameof(BagOfHolding):
                {
                    NextTick(() =>
                    {
                        if (!plugin.IsLoaded)
                            return;

                        _backpackManager.DiscoverBags(plugin);
                    });
                    break;
                }
            }
        }

        private void OnPluginUnloaded(Plugin plugin)
        {
            _subscriberManager.RemoveSubscriber(plugin);
        }

        private void OnPlayerDisconnected(BasePlayer player)
        {
            _backpackCapacityManager.ForgetCachedCapacity(player.userID);
            _backpackManager.GetBackpackIfCached(player.userID)?.NetworkController?.Unsubscribe(player);
        }

        // Handle player death by normal means.
        private void OnEntityDeath(BasePlayer player, HitInfo info) =>
            OnEntityKill(player);

        // Handle player death while sleeping in a safe zone.
        private void OnEntityKill(BasePlayer player)
        {
            if (player.IsNpc)
                return;

            DestroyButtonUi(player);

            if (!_backpackManager.HasBackpackFile(player.userID)
                || permission.UserHasPermission(player.UserIDString, KeepOnDeathPermission))
                return;

            if (_config.EraseOnDeath)
            {
                _backpackManager.TryEraseForPlayer(player.userID);
            }
            else if (_config.DropOnDeath)
            {
                _backpackManager.Drop(player.userID, player.transform.position);
            }
        }

        private void OnGroupPermissionGranted(string groupName, string perm)
        {
            if (!perm.StartsWith("backpacks"))
                return;

            if (perm.StartsWith(SizePermission) || perm.StartsWith(UsagePermission))
            {
                _backpackManager.HandleCapacityPermissionChangedForGroup(groupName);
            }
            else if (perm.StartsWith(RestrictionRuleset.FullPermissionPrefix) || perm.Equals(LegacyNoBlacklistPermission))
            {
                _backpackManager.HandleRestrictionPermissionChangedForGroup(groupName);
            }
            else if (perm.Equals(GatherPermission))
            {
                _backpackManager.HandleGatherPermissionChangedForGroup(groupName);
            }
            else if (perm.Equals(RetrievePermission))
            {
                _backpackManager.HandleRetrievePermissionChangedForGroup(groupName);
            }
            else if (_config.GUI.Enabled && perm.Equals(GUIPermission))
            {
                var groupName2 = groupName;
                foreach (var player in BasePlayer.activePlayerList.Where(p => permission.UserHasGroup(p.UserIDString, groupName2)))
                {
                    CreateOrDestroyButtonUi(player);
                }
            }
        }

        private void OnGroupPermissionRevoked(string groupName, string perm)
        {
            OnGroupPermissionGranted(groupName, perm);
        }

        private void OnUserPermissionGranted(string userId, string perm)
        {
            if (!perm.StartsWith("backpacks"))
                return;

            if (perm.StartsWith(SizePermission) || perm.StartsWith(UsagePermission))
            {
                _backpackManager.HandleCapacityPermissionChangedForUser(userId);
            }
            else if (perm.StartsWith(RestrictionRuleset.FullPermissionPrefix) || perm.Equals(LegacyNoBlacklistPermission))
            {
                _backpackManager.HandleRestrictionPermissionChangedForUser(userId);
            }
            else if (perm.Equals(GatherPermission))
            {
                _backpackManager.HandleGatherPermissionChangedForUser(userId);
            }
            else if (perm.Equals(RetrievePermission))
            {
                _backpackManager.HandleRetrievePermissionChangedForUser(userId);
            }
            else if (_config.GUI.Enabled && perm.Equals(GUIPermission))
            {
                var player = BasePlayer.Find(userId);
                if (player != null)
                {
                    CreateOrDestroyButtonUi(player);
                }
            }
        }

        private void OnUserPermissionRevoked(string userId, string perm)
        {
            OnUserPermissionGranted(userId, perm);
        }

        private void OnUserGroupAdded(string userId, string groupName)
        {
            _backpackManager.HandleGroupChangeForUser(userId);
        }

        private void OnUserGroupRemoved(string userId, string groupName)
        {
            _backpackManager.HandleGroupChangeForUser(userId);
        }

        // Only subscribed while the GUI button is enabled.
        private void OnPlayerConnected(BasePlayer player) => MaybeCreateButtonUi(player);

        private void OnPlayerRespawned(BasePlayer player)
        {
            MaybeCreateButtonUi(player);
            _backpackManager.GetBackpackIfCached(player.userID)?.PauseGatherMode(1f);
        }

        // Only subscribed while the GUI button is enabled.
        private void OnPlayerSleepEnded(BasePlayer player) => OnPlayerRespawned(player);

        // Only subscribed while the GUI button is enabled.
        private void OnPlayerSleep(BasePlayer player) => DestroyButtonUi(player);

        // Only subscribed while the GUI button is enabled.
        private void OnNpcConversationStart(NPCTalking npcTalking, BasePlayer player, ConversationData conversationData)
        {
            // This delay can be removed in the future if an OnNpcConversationStarted hook is created.
            NextTick(() =>
            {
                // Verify the conversation started, since another plugin may have blocked it.
                if (!npcTalking.conversingPlayers.Contains(player))
                    return;

                DestroyButtonUi(player);
            });
        }

        // Only subscribed while the GUI button is enabled.
        private void OnNpcConversationEnded(NPCTalking npcTalking, BasePlayer player) => MaybeCreateButtonUi(player);

        private void OnNetworkSubscriptionsUpdate(Network.Networkable networkable, List<Network.Visibility.Group> groupsToAdd, List<Network.Visibility.Group> groupsToRemove)
        {
            if (groupsToRemove == null)
                return;

            for (var i = groupsToRemove.Count - 1; i >= 0; i--)
            {
                var group = groupsToRemove[i];
                if (BackpackNetworkController.IsBackpackNetworkGroup(group))
                {
                    // Prevent automatically unsubscribing from backpack network groups.
                    // This allows the subscriptions to persist while players move around.
                    groupsToRemove.Remove(group);
                }
            }
        }

        #endregion

        #region API

        private class ApiInstance
        {
            public readonly Dictionary<string, object> ApiWrapper;

            private readonly Backpacks _plugin;
            private BackpackManager _backpackManager => _plugin._backpackManager;

            public ApiInstance(Backpacks plugin)
            {
                _plugin = plugin;

                ApiWrapper = new Dictionary<string, object>
                {
                    [nameof(AddSubscriber)] = new Action<Plugin, Dictionary<string, object>>(AddSubscriber),
                    [nameof(RemoveSubscriber)] = new Action<Plugin>(RemoveSubscriber),
                    [nameof(GetExistingBackpacks)] = new Func<Dictionary<ulong, ItemContainer>>(GetExistingBackpacks),
                    [nameof(EraseBackpack)] = new Action<ulong>(EraseBackpack),
                    [nameof(DropBackpack)] = new Func<BasePlayer, List<DroppedItemContainer>, DroppedItemContainer>(DropBackpack),
                    [nameof(GetBackpackOwnerId)] = new Func<ItemContainer, ulong>(GetBackpackOwnerId),
                    [nameof(IsBackpackLoaded)] = new Func<BasePlayer, bool>(IsBackpackLoaded),
                    [nameof(GetBackpackCapacity)] = new Func<BasePlayer, int>(GetBackpackCapacity),
                    [nameof(IsBackpackGathering)] = new Func<BasePlayer, bool>(IsBackpackGathering),
                    [nameof(IsBackpackRetrieving)] = new Func<BasePlayer, bool>(IsBackpackRetrieving),
                    [nameof(GetBackpackContainer)] = new Func<ulong, ItemContainer>(GetBackpackContainer),
                    [nameof(GetBackpackItemAmount)] = new Func<ulong, int, ulong, int>(GetBackpackItemAmount),
                    [nameof(TryOpenBackpack)] = new Func<BasePlayer, ulong, bool>(TryOpenBackpack),
                    [nameof(TryOpenBackpackContainer)] = new Func<BasePlayer, ulong, ItemContainer, bool>(TryOpenBackpackContainer),
                    [nameof(TryOpenBackpackPage)] = new Func<BasePlayer, ulong, int, bool>(TryOpenBackpackPage),
                    [nameof(SumBackpackItems)] = new Func<ulong, Dictionary<string, object>, int>(SumBackpackItems),
                    [nameof(CountBackpackItems)] = new Func<ulong, Dictionary<string, object>, int>(CountBackpackItems),
                    [nameof(TakeBackpackItems)] = new Func<ulong, Dictionary<string, object>, int, List<Item>, int>(TakeBackpackItems),
                    [nameof(TryDepositBackpackItem)] = new Func<ulong, Item, bool>(TryDepositBackpackItem),
                    [nameof(WriteBackpackContentsFromJson)] = new Action<ulong, string>(WriteBackpackContentsFromJson),
                    [nameof(ReadBackpackContentsAsJson)] = new Func<ulong, string>(ReadBackpackContentsAsJson),
                };
            }

            public void AddSubscriber(Plugin plugin, Dictionary<string, object> spec)
            {
                if (plugin == null)
                    throw new ArgumentNullException(nameof(plugin));

                if (spec == null)
                    throw new ArgumentNullException(nameof(spec));

                _plugin._subscriberManager.AddSubscriber(plugin, spec);
            }

            public void RemoveSubscriber(Plugin plugin)
            {
                if (plugin == null)
                    throw new ArgumentNullException(nameof(plugin));

                _plugin._subscriberManager.RemoveSubscriber(plugin);
            }

            public Dictionary<ulong, ItemContainer> GetExistingBackpacks()
            {
                return _backpackManager.GetAllCachedContainers();
            }

            public void EraseBackpack(ulong userId)
            {
                _backpackManager.TryEraseForPlayer(userId);
            }

            public DroppedItemContainer DropBackpack(BasePlayer player, List<DroppedItemContainer> collect)
            {
                var backpack = _backpackManager.GetBackpackIfExists(player.userID);
                if (backpack == null)
                    return null;

                return _backpackManager.Drop(player.userID, player.transform.position, collect);
            }

            public ulong GetBackpackOwnerId(ItemContainer container)
            {
                return _backpackManager.GetCachedBackpackForContainer(container)?.OwnerId ?? 0;
            }

            public bool IsBackpackLoaded(BasePlayer player)
            {
                return _backpackManager.GetBackpackIfCached(player.userID) != null;
            }

            public int GetBackpackCapacity(BasePlayer player)
            {
                return _plugin._backpackCapacityManager.GetCapacity(player.userID, player.UserIDString);
            }

            public bool IsBackpackGathering(BasePlayer player)
            {
                return _backpackManager.GetBackpackIfCached(player.userID)?.IsGathering ?? false;
            }

            public bool IsBackpackRetrieving(BasePlayer player)
            {
                return _backpackManager.GetBackpackIfCached(player.userID)?.IsRetrieving ?? false;
            }

            public ItemContainer GetBackpackContainer(ulong ownerId)
            {
                return _backpackManager.GetBackpackIfExists(ownerId)?.GetContainer(ensureContainer: true);
            }

            public int GetBackpackItemAmount(ulong ownerId, int itemId, ulong skinId)
            {
                var itemQuery = new ItemQuery { ItemId = itemId, SkinId = skinId };
                return _backpackManager.GetBackpackIfExists(ownerId)?.SumItems(ref itemQuery) ?? 0;
            }

            public bool TryOpenBackpack(BasePlayer player, ulong ownerId)
            {
                return _backpackManager.TryOpenBackpack(player, ownerId);
            }

            public bool TryOpenBackpackContainer(BasePlayer player, ulong ownerId, ItemContainer container)
            {
                return _backpackManager.TryOpenBackpackContainer(player, ownerId, container);
            }

            public bool TryOpenBackpackPage(BasePlayer player, ulong ownerId, int page)
            {
                return _backpackManager.TryOpenBackpackPage(player, ownerId, page);
            }

            public int SumBackpackItems(ulong ownerId, Dictionary<string, object> dict)
            {
                var itemQuery = ItemQuery.Parse(dict);
                return _backpackManager.GetBackpackIfExists(ownerId)?.SumItems(ref itemQuery) ?? 0;
            }

            public int CountBackpackItems(ulong ownerId, Dictionary<string, object> dict)
            {
                var backpack = _backpackManager.GetBackpackIfExists(ownerId);
                if (backpack == null)
                    return 0;

                if (dict == null)
                    return backpack.ItemCount;

                var itemQuery = ItemQuery.Parse(dict);
                return backpack.CountItems(ref itemQuery);
            }

            public int TakeBackpackItems(ulong ownerId, Dictionary<string, object> dict, int amount, List<Item> collect)
            {
                var itemQuery = ItemQuery.Parse(dict);
                return _backpackManager.GetBackpackIfExists(ownerId)?.TakeItems(ref itemQuery, amount, collect) ?? 0;
            }

            public bool TryDepositBackpackItem(ulong ownerId, Item item)
            {
                return _backpackManager.GetBackpack(ownerId).TryDepositItem(item);
            }

            public void WriteBackpackContentsFromJson(ulong ownerId, string json)
            {
                _backpackManager.GetBackpack(ownerId).WriteContentsFromJson(json);
            }

            public string ReadBackpackContentsAsJson(ulong ownerId)
            {
                return _backpackManager.GetBackpackIfExists(ownerId)?.SerializeContentsAsJson();
            }
        }

        [HookMethod(nameof(API_GetApi))]
        public Dictionary<string, object> API_GetApi()
        {
            return _api.ApiWrapper;
        }

        [HookMethod(nameof(API_AddSubscriber))]
        public void API_AddSubscriber(Plugin plugin, Dictionary<string, object> spec)
        {
            _api.AddSubscriber(plugin, spec);
        }

        [HookMethod(nameof(API_RemoveSubscriber))]
        public void API_RemoveSubscriber(Plugin plugin)
        {
            _api.RemoveSubscriber(plugin);
        }

        [HookMethod(nameof(API_GetExistingBackpacks))]
        public Dictionary<ulong, ItemContainer> API_GetExistingBackpacks()
        {
            return _api.GetExistingBackpacks();
        }

        [HookMethod(nameof(API_EraseBackpack))]
        public void API_EraseBackpack(ulong userId)
        {
            _api.EraseBackpack(userId);
        }

        [HookMethod(nameof(API_DropBackpack))]
        public DroppedItemContainer API_DropBackpack(BasePlayer player, List<DroppedItemContainer> collect = null)
        {
            return _api.DropBackpack(player, collect);
        }

        [HookMethod(nameof(API_GetBackpackOwnerId))]
        public object API_GetBackpackOwnerId(ItemContainer container)
        {
            return ObjectCache.Get(_api.GetBackpackOwnerId(container));
        }

        [HookMethod(nameof(API_IsBackpackLoaded))]
        public object API_IsBackpackLoaded(BasePlayer player)
        {
            return ObjectCache.Get(_api.IsBackpackLoaded(player));
        }

        [HookMethod(nameof(API_GetBackpackCapacity))]
        public object API_GetBackpackCapacity(BasePlayer player)
        {
            return ObjectCache.Get(_api.GetBackpackCapacity(player));
        }

        [HookMethod(nameof(API_IsBackpackGathering))]
        public object API_IsBackpackGathering(BasePlayer player)
        {
            return ObjectCache.Get(_api.IsBackpackGathering(player));
        }

        [HookMethod(nameof(API_IsBackpackRetrieving))]
        public object API_IsBackpackRetrieving(BasePlayer player)
        {
            return ObjectCache.Get(_api.IsBackpackRetrieving(player));
        }

        [HookMethod(nameof(API_GetBackpackContainer))]
        public ItemContainer API_GetBackpackContainer(ulong ownerId)
        {
            return _api.GetBackpackContainer(ownerId);
        }

        [HookMethod(nameof(API_GetBackpackItemAmount))]
        public int API_GetBackpackItemAmount(ulong ownerId, int itemId, ulong skinId = 0)
        {
            return _api.GetBackpackItemAmount(ownerId, itemId, skinId);
        }

        [HookMethod(nameof(API_TryOpenBackpack))]
        public object API_TryOpenBackpack(BasePlayer player, ulong ownerId = 0)
        {
            return ObjectCache.Get(_api.TryOpenBackpack(player, ownerId));
        }

        [HookMethod(nameof(API_TryOpenBackpackContainer))]
        public object API_TryOpenBackpackContainer(BasePlayer player, ulong ownerId, ItemContainer container)
        {
            return ObjectCache.Get(_api.TryOpenBackpackContainer(player, ownerId, container));
        }

        [HookMethod(nameof(API_TryOpenBackpackPage))]
        public object API_TryOpenBackpackPage(BasePlayer player, ulong ownerId = 0, int page = 0)
        {
            return ObjectCache.Get(_api.TryOpenBackpackPage(player, ownerId, page));
        }

        [HookMethod(nameof(API_SumBackpackItems))]
        public object API_SumBackpackItems(ulong ownerId, Dictionary<string, object> dict)
        {
            return ObjectCache.Get(_api.SumBackpackItems(ownerId, dict));
        }

        [HookMethod(nameof(API_CountBackpackItems))]
        public object API_CountBackpackItems(ulong ownerId, Dictionary<string, object> dict)
        {
            return ObjectCache.Get(_api.CountBackpackItems(ownerId, dict));
        }

        [HookMethod(nameof(API_TakeBackpackItems))]
        public object API_TakeBackpackItems(ulong ownerId, Dictionary<string, object> dict, int amount, List<Item> collect)
        {
            return ObjectCache.Get(_api.TakeBackpackItems(ownerId, dict, amount, collect));
        }

        [HookMethod(nameof(API_TryDepositBackpackItem))]
        public object API_TryDepositBackpackItem(ulong ownerId, Item item)
        {
            return ObjectCache.Get(_api.TryDepositBackpackItem(ownerId, item));
        }

        [HookMethod(nameof(API_WriteBackpackContentsFromJson))]
        public void API_WriteBackpackContentsFromJson(ulong ownerId, string json)
        {
            _api.WriteBackpackContentsFromJson(ownerId, json);
        }

        [HookMethod(nameof(API_ReadBackpackContentsAsJson))]
        public object API_ReadBackpackContentsAsJson(ulong ownerId)
        {
            return _api.ReadBackpackContentsAsJson(ownerId);
        }

        #endregion

        #region Exposed Hooks

        private static class ExposedHooks
        {
            public static object CanOpenBackpack(BasePlayer looter, ulong ownerId)
            {
                return Interface.CallHook("CanOpenBackpack", looter, ObjectCache.Get(ownerId));
            }

            public static void OnBackpackClosed(BasePlayer looter, ulong ownerId, ItemContainer container)
            {
                Interface.CallHook("OnBackpackClosed", looter, ObjectCache.Get(ownerId), container);
            }

            public static void OnBackpackOpened(BasePlayer looter, ulong ownerId, ItemContainer container)
            {
                Interface.CallHook("OnBackpackOpened", looter, ObjectCache.Get(ownerId), container);
            }

            public static object CanDropBackpack(ulong ownerId, Vector3 position)
            {
                return Interface.CallHook("CanDropBackpack", ObjectCache.Get(ownerId), position);
            }

            public static object CanEraseBackpack(ulong ownerId)
            {
                return Interface.CallHook("CanEraseBackpack", ObjectCache.Get(ownerId));
            }

            public static object CanBackpackAcceptItem(ulong ownerId, ItemContainer container, Item item)
            {
                return Interface.CallHook("CanBackpackAcceptItem", ObjectCache.Get(ownerId), container, item);
            }
        }

        #endregion

        #region Commands

        [Command("backpack", "backpack.open")]
        private void BackpackOpenCommand(IPlayer player, string cmd, string[] args)
        {
            BasePlayer basePlayer;
            if (!VerifyCanInteract(player, out basePlayer)
                || !VerifyHasPermission(player, UsagePermission))
                return;

            OpenBackpack(
                basePlayer,
                IsKeyBindArg(args.LastOrDefault()),
                ParsePageArg(args.FirstOrDefault()),
                wrapAround: false
            );
        }

        [Command("backpack.next")]
        private void BackpackNextCommand(IPlayer player, string cmd, string[] args)
        {
            BasePlayer basePlayer;if (!VerifyCanInteract(player, out basePlayer)
                || !VerifyHasPermission(player, UsagePermission))
                return;

            OpenBackpack(
                basePlayer,
                IsKeyBindArg(args.LastOrDefault())
            );
        }

        [Command("backpack.previous", "backpack.prev")]
        private void BackpackPreviousCommand(IPlayer player, string cmd, string[] args)
        {
            BasePlayer basePlayer;
            if (!VerifyCanInteract(player, out basePlayer)
                || !VerifyHasPermission(player, UsagePermission))
                return;

            OpenBackpack(
                basePlayer,
                IsKeyBindArg(args.LastOrDefault()),
                forward: false
            );
        }

        [Command("backpack.fetch")]
        private void BackpackFetchCommand(IPlayer player, string cmd, string[] args)
        {
            BasePlayer basePlayer;
            if (!VerifyCanInteract(player, out basePlayer)
                || !VerifyHasPermission(player, FetchPermission))
                return;

            if (args.Length < 2)
            {
                player.Reply(GetMessage(player, "Backpack Fetch Syntax"));
                return;
            }

            if (!VerifyCanOpenBackpack(basePlayer, basePlayer.userID))
                return;

            ItemDefinition itemDefinition;
            if (!VerifyValidItem(player, args[0], out itemDefinition))
                return;

            int desiredAmount;
            if (!int.TryParse(args[1], out desiredAmount) || desiredAmount < 1)
            {
                player.Reply(GetMessage(player, "Invalid Item Amount"));
                return;
            }

            var itemLocalizedName = itemDefinition.displayName.translated;
            var backpack = _backpackManager.GetBackpack(basePlayer.userID);

            var itemQuery = new ItemQuery { ItemDefinition = itemDefinition };

            var quantityInBackpack = backpack.SumItems(ref itemQuery);
            if (quantityInBackpack == 0)
            {
                player.Reply(string.Format(GetMessage(player, "Item Not In Backpack"), itemLocalizedName));
                return;
            }

            if (desiredAmount > quantityInBackpack)
            {
                desiredAmount = quantityInBackpack;
            }

            var amountTransferred = backpack.FetchItems(basePlayer, ref itemQuery, desiredAmount);
            if (amountTransferred <= 0)
            {
                player.Reply(string.Format(GetMessage(player, "Fetch Failed"), itemLocalizedName));
                return;
            }

            player.Reply(string.Format(GetMessage(player, "Items Fetched"), amountTransferred.ToString(), itemLocalizedName));
        }

        [Command("backpack.erase")]
        private void EraseBackpackCommand(IPlayer player, string cmd, string[] args)
        {
            if (!player.IsServer)
                return;

            ulong userId;
            if (args.Length < 1 || !ulong.TryParse(args[0], out userId))
            {
                player.Reply($"Syntax: {cmd} <id>");
                return;
            }

            if (!_backpackManager.TryEraseForPlayer(userId))
            {
                LogWarning($"Player {userId.ToString()} has no backpack to erase.");
                return;
            }

            LogWarning($"Erased backpack for player {userId.ToString()}.");
        }

        [Command("viewbackpack")]
        private void ViewBackpackCommand(IPlayer player, string cmd, string[] args)
        {
            BasePlayer basePlayer;
            if (!VerifyCanInteract(player, out basePlayer)
                || !VerifyHasPermission(player, AdminPermission))
                return;

            if (args.Length < 1)
            {
                player.Reply(GetMessage(player, "View Backpack Syntax"));
                return;
            }

            string failureMessage;
            var targetPlayer = FindPlayer(player, args[0], out failureMessage);

            if (targetPlayer == null)
            {
                player.Reply(failureMessage);
                return;
            }

            var targetBasePlayer = targetPlayer.Object as BasePlayer;
            var desiredOwnerId = targetBasePlayer?.userID ?? ulong.Parse(targetPlayer.Id);

            OpenBackpack(
                basePlayer,
                IsKeyBindArg(args.LastOrDefault()),
                ParsePageArg(args.ElementAtOrDefault(1)),
                desiredOwnerId: desiredOwnerId
            );
        }

        // Alias for older versions of Player Administration (which should ideally not be calling this method directly).
        private void ViewBackpack(BasePlayer player, string cmd, string[] args) =>
            ViewBackpackCommand(player.IPlayer, cmd, args);

        private void ToggleBackpackGUICommand(IPlayer player, string cmd, string[] args)
        {
            BasePlayer basePlayer;
            if (!VerifyPlayer(player, out basePlayer)
                || !VerifyHasPermission(player, GUIPermission))
                return;

            var enabledNow = _storedData.ToggleGuiButtonPreference(basePlayer.userID, _config.GUI.EnabledByDefault);
            if (enabledNow)
            {
                MaybeCreateButtonUi(basePlayer);
            }
            else
            {
                DestroyButtonUi(basePlayer);
            }

            player.Reply(GetMessage(player, "Toggled Backpack GUI"));
        }

        [Command("backpack.togglegather")]
        private void ToggleGatherCommand(IPlayer player, string cmd, string[] args)
        {
            BasePlayer basePlayer;
            if (!VerifyPlayer(player, out basePlayer))
                return;

            var lootingContainer = basePlayer.inventory.loot.containers.FirstOrDefault();
            if (lootingContainer == null)
                return;

            Backpack backpack;
            int pageIndex;
            if (!_backpackManager.IsBackpack(lootingContainer, out backpack, out pageIndex)
                || backpack.OwnerId != basePlayer.userID
                || !backpack.CanGather)
                return;

            backpack.ToggleGatherMode(basePlayer, pageIndex);
        }

        [Command("backpack.toggleretrieve")]
        private void ToggleRetrieveCommand(IPlayer player, string cmd, string[] args)
        {
            BasePlayer basePlayer;
            if (!VerifyPlayer(player, out basePlayer))
                return;

            var lootingContainer = basePlayer.inventory.loot.containers.FirstOrDefault();
            if (lootingContainer == null)
                return;

            Backpack backpack;
            int pageIndex;
            if (!_backpackManager.IsBackpack(lootingContainer, out backpack, out pageIndex)
                || pageIndex > 31
                || backpack.OwnerId != basePlayer.userID
                || !backpack.CanRetrieve)
                return;

            backpack.ToggleRetrieve(basePlayer, pageIndex);
        }

        #endregion

        #region Helper Methods

        public static void LogDebug(string message) => Interface.Oxide.LogDebug($"[Backpacks] {message}");
        public static void LogInfo(string message) => Interface.Oxide.LogInfo($"[Backpacks] {message}");
        public static void LogWarning(string message) => Interface.Oxide.LogWarning($"[Backpacks] {message}");
        public static void LogError(string message) => Interface.Oxide.LogError($"[Backpacks] {message}");

        private static T[] ParseEnumList<T>(string[] list, string errorFormat) where T : struct
        {
            var valueList = new List<T>(list?.Length ?? 0);

            if (list != null)
            {
                foreach (var itemName in list)
                {
                    T result;
                    if (Enum.TryParse(itemName, ignoreCase: true, result: out result))
                    {
                        valueList.Add(result);
                    }
                    else
                    {
                        LogError(string.Format(errorFormat, itemName));
                    }
                }
            }

            return valueList.ToArray();
        }

        private static bool IsKeyBindArg(string arg)
        {
            return arg == "True";
        }

        private static int ParsePageArg(string arg)
        {
            if (arg == null)
                return -1;

            int pageIndex;
            return int.TryParse(arg, out pageIndex)
                ? Math.Max(0, pageIndex - 1)
                : -1;
        }

        private static string DetermineLootPanelName(ItemContainer container)
        {
            return (container.entityOwner as StorageContainer)?.panelName
                   ?? (container.entityOwner as ContainerIOEntity)?.lootPanelName
                   ?? (container.entityOwner as LootableCorpse)?.lootPanelName
                   ?? (container.entityOwner as DroppedItemContainer)?.lootPanelName
                   ?? (container.entityOwner as BaseRidableAnimal)?.lootPanelName
                   ?? ResizableLootPanelName;
        }

        private static void ClosePlayerInventory(BasePlayer player)
        {
            player.ClientRPCPlayer(null, player, "OnRespawnInformation");
        }

        private static float CalculateOpenDelay(ItemContainer currentContainer, int nextContainerCapacity, bool isKeyBind = false)
        {
            if (currentContainer != null)
            {
                // Can instantly switch to a smaller container.
                if (nextContainerCapacity <= currentContainer.capacity)
                    return 0;

                // Can instantly switch to a generic resizable loot panel from a different loot panel.
                if (DetermineLootPanelName(currentContainer) != ResizableLootPanelName)
                    return 0;

                // Need a short delay so the generic_resizable loot panel can be redrawn properly.
                return StandardLootDelay;
            }

            // Can open instantly since not looting and chat is assumed to be closed.
            if (isKeyBind)
                return 0;

            // Not opening via key bind, so the chat window may be open.
            // Must delay in case the chat is still closing or else the loot panel may close instantly.
            return StandardLootDelay;
        }

        private static void StartLooting(BasePlayer player, ItemContainer container, StorageContainer entitySource)
        {
            if (player.CanInteract()
                && Interface.CallHook("CanLootEntity", player, entitySource) == null
                && player.inventory.loot.StartLootingEntity(entitySource, doPositionChecks: false))
            {
                player.inventory.loot.AddContainer(container);
                player.inventory.loot.SendImmediate();
                player.ClientRPCPlayer(null, player, "RPC_OpenLootPanel", entitySource.panelName);
            }
        }

        private static ItemContainer CreateItemContainer(int capacity, StorageContainer entityOwner)
        {
            var container = new ItemContainer();
            container.ServerInitialize(null, capacity);
            container.GiveUID();
            container.entityOwner = entityOwner;
            return container;
        }

        private static ItemContainer GetRootContainer(Item item)
        {
            var container = item.parent;
            if (container == null)
                return null;

            while (container.parent?.parent != null && container.parent != item)
            {
                container = container.parent.parent;
            }

            return container;
        }

        private void SendEffect(BasePlayer player, string effectPrefab)
        {
            if (string.IsNullOrWhiteSpace(effectPrefab))
                return;

            _reusableEffect.Init(Effect.Type.Generic, player, 0, Vector3.zero, Vector3.forward);
            _reusableEffect.pooledString = effectPrefab;
            EffectNetwork.Send(_reusableEffect, player.net.connection);
        }

        private void RegisterAsItemSupplier()
        {
            ItemRetriever?.Call("API_AddSupplier", this, new Dictionary<string, object>
            {
                ["FindPlayerItems"] = new Action<BasePlayer, Dictionary<string, object>, List<Item>>((player, rawItemQuery, collect) =>
                {
                    var backpack = _backpackManager.GetBackpackIfCached(player.userID);
                    if (backpack == null || !backpack.CanRetrieve)
                        return;

                    var itemQuery = ItemQuery.Parse(rawItemQuery);
                    backpack.FindItems(ref itemQuery, collect, forItemRetriever: true);
                }),

                ["FindPlayerAmmo"] = new Action<BasePlayer, AmmoTypes, List<Item>>((player, ammoType, collect) =>
                {
                    var backpack = _backpackManager.GetBackpackIfCached(player.userID);
                    if (backpack == null || !backpack.CanRetrieve)
                        return;

                    backpack.FindAmmo(ammoType, collect, forItemRetriever: true);
                }),

                ["SumPlayerItems"] = new Func<BasePlayer, Dictionary<string, object>, int>((player, rawItemQuery) =>
                {
                    var backpack = _backpackManager.GetBackpackIfCached(player.userID);
                    if (backpack == null || !backpack.CanRetrieve)
                        return 0;

                    var itemQuery = ItemQuery.Parse(rawItemQuery);
                    return backpack.SumItems(ref itemQuery, forItemRetriever: true);
                }),

                ["TakePlayerItems"] = new Func<BasePlayer, Dictionary<string, object>, int, List<Item>, int>((player, rawItemQuery, amount, collect) =>
                {
                    var backpack = _backpackManager.GetBackpackIfCached(player.userID);
                    if (backpack == null || !backpack.CanRetrieve)
                        return 0;

                    var itemQuery = ItemQuery.Parse(rawItemQuery);
                    return backpack.TakeItems(ref itemQuery, amount, collect, forItemRetriever: true);
                }),

                ["SerializeForNetwork"] = new Action<BasePlayer, List<ProtoBuf.Item>>((player, saveList) =>
                {
                    var backpack = _backpackManager.GetBackpackIfCached(player.userID);
                    if (backpack == null || !backpack.CanRetrieve)
                        return;

                    backpack.SerializeForNetwork(saveList, forItemRetriever: true);
                }),
            });
        }

        private IEnumerator SaveRoutine(bool async, bool keepInUseBackpacks)
        {
            if (_storedData.SaveIfChanged() && async)
                yield return null;

            yield return _backpackManager.SaveAllAndKill(async, keepInUseBackpacks);
        }

        private void RestartSaveRoutine(bool async, bool keepInUseBackpacks)
        {
            if (_saveRoutine != null)
            {
                ServerMgr.Instance.StopCoroutine(_saveRoutine);
            }

            ServerMgr.Instance?.StartCoroutine(SaveRoutine(async, keepInUseBackpacks));
        }

        private void OpenBackpackMaybeDelayed(BasePlayer looter, ItemContainer currentContainer, Backpack backpack, int pageIndex, bool isKeyBind)
        {
            var pageCapacity = backpack.GetAllowedPageCapacityForLooter(looter.userID, pageIndex);

            var delaySeconds = CalculateOpenDelay(currentContainer, pageCapacity, isKeyBind);
            if (delaySeconds > 0)
            {
                if (currentContainer != null)
                {
                    looter.EndLooting();
                    looter.inventory.loot.SendImmediate();
                }

                var ownerId2 = backpack.OwnerId;
                var looter2 = looter;
                var pageIndex2 = pageIndex;

                timer.Once(delaySeconds, () => _backpackManager.TryOpenBackpackPage(looter2, ownerId2, pageIndex2));
                return;
            }

            _backpackManager.TryOpenBackpackPage(looter, backpack.OwnerId, pageIndex);
        }

        private void OpenBackpack(BasePlayer looter, bool isKeyBind, int desiredPageIndex = -1, bool forward = true, bool wrapAround = true, ulong desiredOwnerId = 0)
        {
            var playerLoot = looter.inventory.loot;
            var lootingContainer = playerLoot.containers.FirstOrDefault();

            if (lootingContainer != null)
            {
                Backpack currentBackpack;
                int currentPageIndex;
                if (_backpackManager.IsBackpack(lootingContainer, out currentBackpack, out currentPageIndex)
                    && (currentBackpack.OwnerId == desiredOwnerId || desiredOwnerId == 0))
                {
                    var nextPageIndex = currentBackpack.DetermineNextPageIndexForLooter(looter.userID, currentPageIndex, desiredPageIndex, forward, wrapAround, requireContents: false);
                    if (nextPageIndex == currentPageIndex)
                    {
                        if (!wrapAround)
                        {
                            // Close the backpack.
                            looter.EndLooting();
                            ClosePlayerInventory(looter);
                        }
                        return;
                    }

                    var nextPageCapacity = currentBackpack.GetAllowedPageCapacityForLooter(looter.userID, nextPageIndex);
                    if (nextPageCapacity > lootingContainer.capacity)
                    {
                        playerLoot.Clear();
                        playerLoot.SendImmediate();

                        {
                            var backpack2 = currentBackpack;
                            var looter2 = looter;
                            var pageIndex2 = desiredPageIndex;
                            timer.Once(StandardLootDelay, () => backpack2.TryOpen(looter2, pageIndex2));
                        }
                        return;
                    }

                    currentBackpack.SwitchToPage(looter, nextPageIndex);
                    return;
                }

                var parent = lootingContainer.parent?.parent;
                if (parent != null && _backpackManager.IsBackpack(parent, out currentBackpack, out currentPageIndex)
                    && (currentBackpack.OwnerId == desiredOwnerId || desiredOwnerId == 0))
                {
                    // Player is looting a child container of the target backpack, so open the current page.
                    OpenBackpackMaybeDelayed(looter, lootingContainer, currentBackpack, currentPageIndex, isKeyBind);
                    return;
                }
            }

            // At this point, player is not looting, looting a different backpack, or looting a different container.
            if (desiredOwnerId == 0)
            {
                desiredOwnerId = looter.userID;
            }

            var backpack = _backpackManager.GetBackpack(desiredOwnerId);
            desiredPageIndex = backpack.DetermineInitialPageForLooter(looter.userID, desiredPageIndex, forward);

            OpenBackpackMaybeDelayed(looter, lootingContainer, backpack, desiredPageIndex, isKeyBind);
        }

        private bool ShouldDisplayGuiButton(BasePlayer player)
        {
            return _storedData.GetGuiButtonPreference(player.userID)
                ?? _config.GUI.EnabledByDefault;
        }

        private IPlayer FindPlayer(IPlayer requester, string nameOrID, out string failureMessage)
        {
            failureMessage = string.Empty;

            ulong userId;
            if (nameOrID.StartsWith("7656119") && nameOrID.Length == 17 && ulong.TryParse(nameOrID, out userId))
            {
                IPlayer player = covalence.Players.All.FirstOrDefault(p => p.Id == nameOrID);

                if (player == null)
                {
                    failureMessage = string.Format(GetMessage(requester, "User ID not Found"), nameOrID);
                }

                return player;
            }

            var foundPlayers = new List<IPlayer>();

            foreach (var player in covalence.Players.All)
            {
                if (player.Name.Equals(nameOrID, StringComparison.InvariantCultureIgnoreCase))
                    return player;

                if (player.Name.ToLower().Contains(nameOrID.ToLower()))
                {
                    foundPlayers.Add(player);
                }
            }

            switch (foundPlayers.Count)
            {
                case 0:
                    failureMessage = string.Format(GetMessage(requester, "User Name not Found"), nameOrID);
                    return null;

                case 1:
                    return foundPlayers[0];

                default:
                    string names = string.Join(", ", foundPlayers.Select(p => p.Name).ToArray());
                    failureMessage = string.Format(GetMessage(requester, "Multiple Players Found"), names);
                    return null;
            }
        }

        private bool VerifyPlayer(IPlayer player, out BasePlayer basePlayer)
        {
            if (player.IsServer)
            {
                basePlayer = null;
                return false;
            }

            basePlayer = player.Object as BasePlayer;
            return true;
        }

        private bool VerifyHasPermission(IPlayer player, string perm)
        {
            if (player.HasPermission(perm))
                return true;

            player.Reply(GetMessage(player, "No Permission"));
            return false;
        }

        private bool VerifyValidItem(IPlayer player, string itemArg, out ItemDefinition itemDefinition)
        {
            itemDefinition = ItemManager.FindItemDefinition(itemArg);
            if (itemDefinition != null)
                return true;

            // User may have provided an itemID instead of item short name
            int itemID;
            if (!int.TryParse(itemArg, out itemID))
            {
                player.Reply(GetMessage(player, "Invalid Item"));
                return false;
            }

            itemDefinition = ItemManager.FindItemDefinition(itemID);
            if (itemDefinition != null)
                return true;

            player.Reply(GetMessage(player, "Invalid Item"));
            return false;
        }

        private bool VerifyCanInteract(IPlayer player, out BasePlayer basePlayer)
        {
            return VerifyPlayer(player, out basePlayer)
                   && basePlayer.CanInteract();
        }

        private bool VerifyCanOpenBackpack(BasePlayer looter, ulong ownerId)
        {
            if (IsPlayingEvent(looter))
            {
                looter.ChatMessage(GetMessage(looter, "May Not Open Backpack In Event"));
                return false;
            }

            var hookResult = ExposedHooks.CanOpenBackpack(looter, ownerId);
            if (hookResult != null && hookResult is string)
            {
                looter.ChatMessage(hookResult as string);
                return false;
            }

            return true;
        }

        private bool IsPlayingEvent(BasePlayer player)
        {
            // Multiple event/arena plugins define the isEventPlayer method as a standard.
            var isPlaying = Interface.CallHook("isEventPlayer", player);
            if (isPlaying is bool && (bool)isPlaying)
                return true;

            if (EventManager != null)
            {
                // EventManager 3.x
                isPlaying = EventManager.Call("isPlaying", player);
                if (isPlaying is bool && (bool)isPlaying)
                    return true;
            }

            if (Arena != null)
            {
                isPlaying = Arena.Call("IsEventPlayer", player);
                if (isPlaying is bool && (bool)isPlaying)
                    return true;
            }

            return false;
        }

        private void MaybeCreateButtonUi(BasePlayer player)
        {
            if (!_config.GUI.Enabled)
                return;

            if (player == null || player.IsNpc || !player.IsAlive() || player.IsSleeping())
                return;

            if (!permission.UserHasPermission(player.UserIDString, GUIPermission))
                return;

            if (!ShouldDisplayGuiButton(player))
                return;

            _uiViewers.Add(player.userID);

            if (_cachedButtonUi == null)
            {
                _cachedButtonUi = ButtonUi.CreateButtonUi(_config);
            }

            CuiHelper.AddUi(player, _cachedButtonUi);
        }

        private void DestroyButtonUi(BasePlayer player)
        {
            if (!_uiViewers.Remove(player.userID))
                return;

            ButtonUi.DestroyUi(player);
        }

        private void CreateOrDestroyButtonUi(BasePlayer player)
        {
            if (permission.UserHasPermission(player.UserIDString, GUIPermission))
            {
                MaybeCreateButtonUi(player);
            }
            else
            {
                DestroyButtonUi(player);
            }
        }

        #endregion

        #region Helper Classes

        private static class StringUtils
        {
            public static bool Equals(string a, string b) =>
                string.Compare(a, b, StringComparison.OrdinalIgnoreCase) == 0;

            public static bool Contains(string haystack, string needle) =>
                haystack.Contains(needle, CompareOptions.IgnoreCase);
        }

        private static class ObjectCache
        {
            private static readonly object True = true;
            private static readonly object False = false;

            private static class StaticObjectCache<T>
            {
                private static readonly Dictionary<T, object> _cacheByValue = new Dictionary<T, object>();

                public static object Get(T value)
                {
                    object cachedObject;
                    if (!_cacheByValue.TryGetValue(value, out cachedObject))
                    {
                        cachedObject = value;
                        _cacheByValue[value] = cachedObject;
                    }
                    return cachedObject;
                }
            }

            public static object Get<T>(T value)
            {
                return StaticObjectCache<T>.Get(value);
            }

            public static object Get(bool value)
            {
                return value ? True : False;
            }
        }

        private class PoolConverter<T> : CustomCreationConverter<T> where T : class, new()
        {
            public override T Create(Type objectType)
            {
                #if DEBUG_POOLING
                LogDebug($"{typeof(PoolConverter<T>).Name}<{objectType.Name}>::Create");
                #endif

                return Pool.Get<T>();
            }
        }

        private class PoolListConverter<T> : CustomCreationConverter<List<T>> where T : class, new()
        {
            public override List<T> Create(Type objectType)
            {
                #if DEBUG_POOLING
                LogDebug($"{typeof(PoolListConverter<T>).Name}<{objectType.Name}>::Create");
                #endif

                return Pool.GetList<T>();
            }
        }

        private static class ItemUtils
        {
            public static int PositionOf(List<Item> itemList, ref ItemQuery itemQuery)
            {
                // Assumes the list is sorted.
                foreach (var item in itemList)
                {
                    var usableAmount = itemQuery.GetUsableAmount(item);
                    if (usableAmount > 0)
                        return item.position;
                }

                return -1;
            }

            public static int PositionOf(List<ItemData> itemDataList, ref ItemQuery itemQuery)
            {
                // Assumes the list is sorted.
                foreach (var itemData in itemDataList)
                {
                    var usableAmount = itemQuery.GetUsableAmount(itemData);
                    if (usableAmount > 0)
                        return itemData.Position;
                }

                return -1;
            }

            public static void FindItems(List<Item> itemList, ref ItemQuery itemQuery, List<Item> collect)
            {
                foreach (var item in itemList)
                {
                    var usableAmount = itemQuery.GetUsableAmount(item);
                    if (usableAmount > 0)
                    {
                        collect.Add(item);
                    }

                    List<Item> childItemList;
                    if (HasSearchableContainer(item, out childItemList))
                    {
                        FindItems(childItemList, ref itemQuery, collect);
                    }
                }
            }

            public static int CountItems(List<Item> itemList, ref ItemQuery itemQuery)
            {
                var count = 0;

                foreach (var item in itemList)
                {
                    var usableAmount = itemQuery.GetUsableAmount(item);
                    if (usableAmount > 0)
                    {
                        count++;
                    }

                    List<Item> childItems;
                    if (HasSearchableContainer(item, out childItems))
                    {
                        count += CountItems(childItems, ref itemQuery);
                    }
                }

                return count;
            }

            public static int CountItems(List<ItemData> itemDataList, ref ItemQuery itemQuery)
            {
                var count = 0;

                foreach (var itemData in itemDataList)
                {
                    var usableAmount = itemQuery.GetUsableAmount(itemData);
                    if (usableAmount > 0)
                    {
                        count++;
                    }

                    List<ItemData> childItems;
                    if (HasSearchableContainer(itemData, out childItems))
                    {
                        count += CountItems(childItems, ref itemQuery);
                    }
                }

                return count;
            }

            public static int SumItems(List<Item> itemList, ref ItemQuery itemQuery)
            {
                var sum = 0;

                foreach (var item in itemList)
                {
                    sum += itemQuery.GetUsableAmount(item);

                    List<Item> childItems;
                    if (HasSearchableContainer(item, out childItems))
                    {
                        sum += SumItems(childItems, ref itemQuery);
                    }
                }

                return sum;
            }

            public static int SumItems(List<ItemData> itemDataList, ref ItemQuery itemQuery)
            {
                var sum = 0;

                foreach (var itemData in itemDataList)
                {
                    sum += itemQuery.GetUsableAmount(itemData);

                    List<ItemData> childItemList;
                    if (HasSearchableContainer(itemData, out childItemList))
                    {
                        sum += SumItems(childItemList, ref itemQuery);
                    }
                }

                return sum;
            }

            public static int TakeItems(List<Item> itemList, ref ItemQuery itemQuery, int amount, List<Item> collect)
            {
                var totalAmountTaken = 0;

                for (var i = itemList.Count - 1; i >= 0; i--)
                {
                    var item = itemList[i];
                    var amountToTake = amount - totalAmountTaken;
                    if (amountToTake <= 0)
                        break;

                    var usableAmount = itemQuery.GetUsableAmount(item);
                    if (usableAmount > 0)
                    {
                        amountToTake = Math.Min(usableAmount, amountToTake);

                        TakeItemAmount(item, amountToTake, collect);
                        totalAmountTaken += amountToTake;
                    }

                    amountToTake = amount - totalAmountTaken;
                    List<Item> childItemList;
                    if (amountToTake > 0 && HasSearchableContainer(item, out childItemList))
                    {
                        totalAmountTaken += TakeItems(childItemList, ref itemQuery, amountToTake, collect);
                    }
                }

                return totalAmountTaken;
            }

            public static int TakeItems(List<ItemData> itemDataList, ref ItemQuery itemQuery, int amount, List<Item> collect)
            {
                var totalAmountTaken = 0;

                for (var i = itemDataList.Count - 1; i >= 0; i--)
                {
                    var itemData = itemDataList[i];
                    var amountToTake = amount - totalAmountTaken;
                    if (amountToTake <= 0)
                        break;

                    var usableAmount = itemQuery.GetUsableAmount(itemData);
                    if (usableAmount > 0)
                    {
                        amountToTake = Math.Min(usableAmount, amountToTake);

                        collect?.Add(itemData.ToItem(amountToTake));
                        itemData.Reduce(amountToTake);

                        if (itemData.Amount <= 0)
                        {
                            itemDataList.RemoveAt(i);
                            Pool.Free(ref itemData);
                        }

                        totalAmountTaken += amountToTake;
                    }

                    amountToTake = amount - totalAmountTaken;
                    List<ItemData> childItemList;
                    if (amountToTake > 0 && HasSearchableContainer(itemData, out childItemList))
                    {
                        totalAmountTaken += TakeItems(childItemList, ref itemQuery, amountToTake, collect);
                    }
                }

                return totalAmountTaken;
            }

            public static void SerializeForNetwork(List<Item> itemList, List<ProtoBuf.Item> collect)
            {
                foreach (var item in itemList)
                {
                    collect.Add(item.Save());

                    List<Item> childItems;
                    if (HasSearchableContainer(item, out childItems))
                    {
                        SerializeForNetwork(childItems, collect);
                    }
                }
            }

            public static void SerializeForNetwork(List<ItemData> itemDataList, List<ProtoBuf.Item> collect)
            {
                foreach (var itemData in itemDataList)
                {
                    var serializedItemData = Pool.Get<ProtoBuf.Item>();
                    serializedItemData.itemid = itemData.ID;
                    serializedItemData.amount = itemData.Amount;

                    if (itemData.DataInt != 0 || itemData.BlueprintTarget != 0)
                    {
                        if (serializedItemData.instanceData == null)
                        {
                            serializedItemData.instanceData = Pool.Get<ProtoBuf.Item.InstanceData>();
                        }

                        serializedItemData.instanceData.dataInt = itemData.DataInt;
                        serializedItemData.instanceData.blueprintTarget = itemData.BlueprintTarget;
                    }

                    collect.Add(serializedItemData);

                    List<ItemData> childItemList;
                    if (HasSearchableContainer(itemData, out childItemList))
                    {
                        SerializeForNetwork(childItemList, collect);
                    }
                }
            }

            private static bool HasItemMod<T>(ItemDefinition itemDefinition) where T : ItemMod
            {
                foreach (var itemMod in itemDefinition.itemMods)
                {
                    if (itemMod is T)
                        return true;
                }

                return false;
            }

            private static bool HasSearchableContainer(ItemDefinition itemDefinition)
            {
                // Don't consider vanilla containers searchable (i.e., don't take low grade out of a miner's hat).
                return !HasItemMod<ItemModContainer>(itemDefinition);
            }

            private static bool HasSearchableContainer(Item item, out List<Item> itemList)
            {
                itemList = item.contents?.itemList;
                return itemList?.Count > 0 && !item.HasFlag(UnsearchableItemFlag) && HasSearchableContainer(item.info);
            }

            private static bool HasSearchableContainer(int itemId)
            {
                var itemDefinition = ItemManager.FindItemDefinition(itemId);
                if ((object)itemDefinition == null)
                    return false;

                return HasSearchableContainer(itemDefinition);
            }

            private static bool HasSearchableContainer(ItemData itemData, out List<ItemData> itemDataList)
            {
                itemDataList = itemData.Contents;
                return itemDataList?.Count > 0 && !itemData.Flags.HasFlag(UnsearchableItemFlag) && HasSearchableContainer(itemData.ID);
            }

            private static void TakeItemAmount(Item item, int amount, List<Item> collect)
            {
                if (amount >= item.amount)
                {
                    item.RemoveFromContainer();
                    if (collect != null)
                    {
                        collect.Add(item);
                    }
                    else
                    {
                        item.Remove();
                    }
                }
                else
                {
                    if (collect != null)
                    {
                        collect.Add(item.SplitItem(amount));
                    }
                    else
                    {
                        item.amount -= amount;
                        item.MarkDirty();
                    }
                }
            }
        }

        #endregion

        #region Pooling

        private static class PoolUtils
        {
            public const int BackpackPoolSize = 500;

            public static void ResetItemsAndClear<T>(IList<T> list) where T : class, Pool.IPooled
            {
                for (var i = list.Count - 1; i >= 0; i--)
                {
                    var item = list[i];
                    if (item == null)
                        continue;

                    Pool.Free(ref item);
                }

                if (list.IsReadOnly)
                {
                    for (var i = 0; i < list.Count; i++)
                    {
                        list[i] = null;
                    }
                }
                else
                {
                    list.Clear();
                }
            }

            public static void ResizePools(bool empty = false)
            {
                ResetPool<ItemData>(empty ? 0 : 2 * BackpackPoolSize);
                ResetPool<List<ItemData>>(empty ? 0 : BackpackPoolSize);
                ResetPool<EntityData>(empty ? 0 : BackpackPoolSize / 4);
                ResetPool<Backpack>(empty ? 0 : BackpackPoolSize);
                ResetPool<VirtualContainerAdapter>(empty ? 0 : 2 * BackpackPoolSize);
                ResetPool<ItemContainerAdapter>(empty ? 0 : 2 * BackpackPoolSize);
                ResetPool<DisposableList<Item>>(empty ? 0 : 4);
                ResetPool<DisposableList<ItemData>>(empty ? 0 : 4);
            }

            #if DEBUG_POOLING
            public static string GetStats<T>() where T : class
            {
                var pool = Pool.FindCollection<T>();
                return $"{typeof(T).Name} | {pool.ItemsInUse.ToString()} used of {pool.ItemsCreated.ToString()} created | {pool.ItemsTaken.ToString()} taken";
            }
            #endif

            private static void ResetPool<T>(int size = 512) where T : class
            {
                var pool = Pool.FindCollection<T>();
                pool.Reset();
                pool.buffer = new T[size];
                Pool.directory.Remove(typeof(T));
            }
        }

        private class DisposableList<T> : List<T>, IDisposable
        {
            public static DisposableList<T> Get()
            {
                return Pool.Get<DisposableList<T>>();
            }

            public void Dispose()
            {
                Clear();
                var self = this;
                Pool.Free(ref self);
            }
        }

        #endregion

        #region String Cache

        private interface IStringCache
        {
            string Get<T>(T value);
            string Get<T>(T value, Func<T, string> createString);
            string Get(bool value);
        }

        private sealed class DefaultStringCache : IStringCache
        {
            public static readonly DefaultStringCache Instance = new DefaultStringCache();

            private static class StaticStringCache<T>
            {
                private static readonly Dictionary<T, string> _cacheByValue = new Dictionary<T, string>();

                public static string Get(T value)
                {
                    string str;
                    if (!_cacheByValue.TryGetValue(value, out str))
                    {
                        str = value.ToString();
                        _cacheByValue[value] = str;
                    }

                    return str;
                }
            }

            private static class StaticStringCacheWithFactory<T>
            {
                private static readonly Dictionary<Func<T, string>, Dictionary<T, string>> _cacheByDelegate =
                    new Dictionary<Func<T, string>, Dictionary<T, string>>();

                public static string Get(T value, Func<T, string> createString)
                {
                    if (createString.Target != null)
                        throw new InvalidOperationException($"{typeof(StaticStringCacheWithFactory<T>).Name} only accepts open delegates");

                    Dictionary<T, string> cache;
                    if (!_cacheByDelegate.TryGetValue(createString, out cache))
                    {
                        cache = new Dictionary<T, string>();
                        _cacheByDelegate[createString] = cache;
                    }

                    string str;
                    if (!cache.TryGetValue(value, out str))
                    {
                        str = createString(value);
                        cache[value] = str;
                    }

                    return str;
                }
            }

            private DefaultStringCache() {}

            public string Get<T>(T value)
            {
                return StaticStringCache<T>.Get(value);
            }

            public string Get(bool value)
            {
                return value ? "true" : "false";
            }

            public string Get<T>(T value, Func<T, string> createString)
            {
                return StaticStringCacheWithFactory<T>.Get(value, createString);
            }
        }

        #endregion

        #region UI Builder

        private interface IUiSerializable
        {
            void Serialize(IUiBuilder uiBuilder);
        }

        private interface IUiBuilder
        {
            IStringCache StringCache { get; }
            void Start();
            void End();
            void StartElement();
            void EndElement();
            void StartComponent();
            void EndComponent();
            void AddField<T>(string key, T value);
            void AddField(string key, string value);
            void AddXY(string key, float x, float y);
            void AddSerializable<T>(T serializable) where T : IUiSerializable;
            void AddComponents<T>(T components) where T : IUiComponentCollection;
            string ToJson();
            byte[] GetBytes();
            void AddUi(SendInfo sendInfo);
            void AddUi(BasePlayer player);
        }

        private class UiBuilder : IUiBuilder
        {
            private static NetWrite ClientRPCStart(BaseEntity entity, string funcName)
            {
                if (Net.sv.IsConnected() && entity.net != null)
                {
                    var write = Net.sv.StartWrite();
                    write.PacketID(Message.Type.RPCMessage);
                    write.UInt32(entity.net.ID);
                    write.UInt32(StringPool.Get(funcName));
                    write.UInt64(0);
                    return write;
                }
                return null;
            }

            public static readonly UiBuilder Default = new UiBuilder(65536);

            private enum State
            {
                Empty,
                ElementList,
                Element,
                ComponentList,
                Component,
                Complete
            }

            public int Length { get; private set; }

            private const char Delimiter = ',';
            private const char Quote = '"';
            private const char Colon = ':';
            private const char Space = ' ';
            private const char OpenBracket = '[';
            private const char CloseBracket = ']';
            private const char OpenCurlyBrace = '{';
            private const char CloseCurlyBrace = '}';

            private const int MinCapacity = 1024;
            private const int DefaultCapacity = 4096;

            public IStringCache StringCache { get; }
            private char[] _chars;
            private byte[] _bytes;
            private State _state;
            private bool _needsDelimiter;

            public UiBuilder(int capacity, IStringCache stringCache)
            {
                if (capacity < MinCapacity)
                    throw new InvalidOperationException($"Capacity must be at least {MinCapacity}");

                Resize(capacity);
                StringCache = stringCache;
            }

            public UiBuilder(int capacity = DefaultCapacity) : this(capacity, DefaultStringCache.Instance) {}

            public void Start()
            {
                Reset();
                StartArray();
                _state = State.ElementList;
            }

            public void End()
            {
                ValidateState(State.ElementList);
                EndArray();
                _state = State.Complete;
            }

            public void StartElement()
            {
                ValidateState(State.ElementList);
                StartObject();
                _state = State.Element;
            }

            public void EndElement()
            {
                ValidateState(State.Element);
                EndObject();
                _state = State.ElementList;
            }

            public void StartComponent()
            {
                ValidateState(State.ComponentList);
                StartObject();
                _state = State.Component;
            }

            public void EndComponent()
            {
                ValidateState(State.Component);
                EndObject();
                _state = State.ComponentList;
            }

            public void AddField<T>(string key, T value)
            {
                AddKey(key);
                Append(StringCache.Get(value));
                _needsDelimiter = true;
            }

            public void AddField(string key, string value)
            {
                if (value == null)
                    return;

                AddKey(key);
                Append(Quote);
                Append(value);
                Append(Quote);
                _needsDelimiter = true;
            }

            public void AddXY(string key, float x, float y)
            {
                AddKey(key);
                Append(Quote);
                Append(StringCache.Get(x));
                Append(Space);
                Append(StringCache.Get(y));
                Append(Quote);
                _needsDelimiter = true;
            }

            public void AddSerializable<T>(T serializable) where T : IUiSerializable
            {
                serializable.Serialize(this);
            }

            public void AddComponents<T>(T components) where T : IUiComponentCollection
            {
                ValidateState(State.Element);
                AddKey("components");
                StartArray();
                _state = State.ComponentList;
                components.Serialize(this);
                EndArray();
                _state = State.Element;
            }

            public string ToJson()
            {
                ValidateState(State.Complete);
                return new string(_chars, 0, Length);
            }

            public byte[] GetBytes()
            {
                ValidateState(State.Complete);
                var bytes = new byte[Length];
                Buffer.BlockCopy(_bytes, 0, bytes, 0, Length);
                return bytes;
            }

            public void AddUi(SendInfo sendInfo)
            {
                var write = ClientRPCStart(CommunityEntity.ServerInstance, "AddUI");
                if (write != null)
                {
                    var byteCount = Encoding.UTF8.GetBytes(_chars, 0, Length, _bytes, 0);
                    write.BytesWithSize(_bytes, byteCount);
                    write.Send(sendInfo);
                }
            }

            public void AddUi(BasePlayer player)
            {
                AddUi(new SendInfo(player.Connection));
            }

            private void ValidateState(State desiredState)
            {
                if (_state != desiredState)
                    throw new InvalidOperationException($"Expected state {desiredState} but found {_state}");
            }

            private void ValidateState(State desiredState, State alternateState)
            {
                if (_state != desiredState && _state != alternateState)
                    throw new InvalidOperationException($"Expected state {desiredState} or {alternateState} but found {_state}");
            }

            private void Resize(int length)
            {
                Array.Resize(ref _chars, length);
                Array.Resize(ref _bytes, length * 2);
            }

            private void ResizeIfApproachingLength()
            {
                if (Length + 1024 > _chars.Length)
                {
                    Resize(_chars.Length * 2);
                }
            }

            private void Append(char @char)
            {
                _chars[Length++] = @char;
            }

            private void Append(string str)
            {
                for (var i = 0; i < str.Length; i++)
                {
                    _chars[Length + i] = str[i];
                }

                Length += str.Length;
            }

            private void AddDelimiter()
            {
                Append(Delimiter);
            }

            private void AddDelimiterIfNeeded()
            {
                if (_needsDelimiter)
                {
                    AddDelimiter();
                }
            }

            private void StartObject()
            {
                AddDelimiterIfNeeded();
                Append(OpenCurlyBrace);
                _needsDelimiter = false;
            }

            private void EndObject()
            {
                Append(CloseCurlyBrace);
                _needsDelimiter = true;
            }

            private void StartArray()
            {
                Append(OpenBracket);
                _needsDelimiter = false;
            }

            private void EndArray()
            {
                Append(CloseBracket);
                _needsDelimiter = true;
            }

            private void AddKey(string key)
            {
                ValidateState(State.Element, State.Component);
                ResizeIfApproachingLength();
                AddDelimiterIfNeeded();
                Append(Quote);
                Append(key);
                Append(Quote);
                Append(Colon);
            }

            private void Reset()
            {
                Length = 0;
                _state = State.Empty;
                _needsDelimiter = false;
            }
        }

        #endregion

        #region UI Layout

        private struct UiRect
        {
            public string Anchor;
            public float XMin;
            public float XMax;
            public float YMin;
            public float YMax;
        }

        private static class Layout
        {
            [Flags]
            public enum Option
            {
                AnchorBottom = 1 << 0,
                AnchorRight = 1 << 1,
                Vertical = 1 << 2
            }

            public const string AnchorBottomLeft = "0 0";
            public const string AnchorBottomRight = "1 0";
            public const string AnchorTopLeft = "0 1";
            public const string AnchorTopRight = "1 1";

            public const string AnchorBottomCenter = "0.5 0";
            public const string AnchorTopCenter = "0.5 1";
            public const string AnchorCenterLeft = "0 0.5";
            public const string AnchorCenterRight = "1 0.5";

            public static string DetermineAnchor(Option options)
            {
                return options.HasFlag(Option.AnchorBottom)
                    ? options.HasFlag(Option.AnchorRight) ? AnchorBottomRight : AnchorBottomLeft
                    : options.HasFlag(Option.AnchorRight) ? AnchorTopRight : AnchorTopLeft;
            }
        }

        private interface ILayoutProvider {}

        private struct StatelessLayoutProvider : ILayoutProvider
        {
            public static UiRect GetRect(int index, Layout.Option options, Vector2 size, float spacing = 0, Vector2 offset = default(Vector2))
            {
                var xMin = !options.HasFlag(Layout.Option.Vertical)
                    ? offset.x + index * (spacing + size.x)
                    : offset.x;

                var xMax = xMin + size.x;

                var yMin = options.HasFlag(Layout.Option.Vertical)
                    ? offset.y + index * (spacing + size.y)
                    : offset.y;

                var yMax = yMin + size.y;

                if (options.HasFlag(Layout.Option.AnchorRight))
                {
                    var temp = xMin;
                    xMin = -xMax;
                    xMax = -temp;
                }

                if (!options.HasFlag(Layout.Option.AnchorBottom))
                {
                    var temp = yMin;
                    yMin = -yMax;
                    yMax = -temp;
                }

                return new UiRect
                {
                    Anchor = Layout.DetermineAnchor(options),
                    XMin = xMin,
                    XMax = xMax,
                    YMin = yMin,
                    YMax = yMax,
                };
            }

            public Layout.Option Options;
            public Vector2 Offset;
            public Vector2 Size;
            public float Spacing;

            public UiRect this[int index] => GetRect(index, Options, Size, Spacing, Offset);

            public static StatelessLayoutProvider operator +(StatelessLayoutProvider layoutProvider, Vector2 vector)
            {
                layoutProvider.Offset += vector;
                return layoutProvider;
            }

            public static StatelessLayoutProvider operator -(StatelessLayoutProvider layoutProvider, Vector2 vector)
            {
                layoutProvider.Offset -= vector;
                return layoutProvider;
            }
        }

        private struct StatefulLayoutProvider : ILayoutProvider
        {
            public Layout.Option Options;
            public Vector2 Offset;
            public Vector2 Size;
            public float Spacing;

            public static StatefulLayoutProvider operator +(StatefulLayoutProvider layoutProvider, Vector2 vector)
            {
                layoutProvider.Offset += vector;
                return layoutProvider;
            }

            public static StatefulLayoutProvider operator -(StatefulLayoutProvider layoutProvider, Vector2 vector)
            {
                layoutProvider.Offset -= vector;
                return layoutProvider;
            }

            public UiRect Current(Vector2 size)
            {
                return StatelessLayoutProvider.GetRect(0, Options, size, Spacing, Offset);
            }

            public UiRect Current()
            {
                return Current(Size);
            }

            public UiRect Next(Vector2 size)
            {
                var position = Current(size);

                if (Options.HasFlag(Layout.Option.Vertical))
                {
                    Offset.y += size.y + Spacing;
                }
                else
                {
                    Offset.x += size.x + Spacing;
                }

                return position;
            }

            public UiRect Next(float x, float y)
            {
                return Next(new Vector2(x, y));
            }

            public UiRect Next()
            {
                return Next(Size);
            }
        }

        #endregion

        #region UI Components

        private interface IUiComponent : IUiSerializable {}

        private struct UiButtonComponent : IUiComponent
        {
            private const string Type = "UnityEngine.UI.Button";

            private const string DefaultCommand = null;
            private const string DefaultClose = null;
            private const string DefaultSprite = "Assets/Content/UI/UI.Background.Tile.psd";
            private const string DefaultMaterial = "Assets/Icons/IconMaterial.mat";
            private const string DefaultColor = "1 1 1 1";
            private const Image.Type DefaultImageType = Image.Type.Simple;
            private const float DefaultFadeIn = 0;

            public string Command;
            public string Close;
            public string Sprite;
            public string Material;
            public string Color;
            public Image.Type ImageType;
            public float FadeIn;

            public void Serialize(IUiBuilder builder)
            {
                if (Sprite == default(string))
                    Sprite = DefaultSprite;

                if (Material == default(string))
                    Material = DefaultMaterial;

                if (Color == default(string))
                    Color = DefaultColor;

                if (ImageType == default(Image.Type))
                    ImageType = DefaultImageType;

                builder.StartComponent();
                builder.AddField("type", Type);

                if (Command != DefaultCommand)
                    builder.AddField("command", Command);

                if (Close != DefaultClose)
                    builder.AddField("close", Close);

                if (Sprite != DefaultSprite)
                    builder.AddField("sprite", Sprite);

                if (Material != DefaultMaterial)
                    builder.AddField("material", Material);

                if (Color != DefaultColor)
                    builder.AddField("color", Color);

                if (ImageType != DefaultImageType)
                    builder.AddField("imagetype", builder.StringCache.Get(ImageType));

                if (FadeIn != DefaultFadeIn)
                    builder.AddField("fadeIn", FadeIn);

                builder.EndComponent();
            }
        }

        private struct UiImageComponent : IUiComponent
        {
            private const string Type = "UnityEngine.UI.Image";

            private const string DefaultSprite = "Assets/Content/UI/UI.Background.Tile.psd";
            private const string DefaultMaterial = "Assets/Icons/IconMaterial.mat";
            private const string DefaultColor = "1 1 1 1";
            private const Image.Type DefaultImageType = Image.Type.Simple;
            private const string DefaultPng = null;
            private const int DefaultItemId = 0;
            private const ulong DefaultSkinId = 0;
            private const float DefaultFadeIn = 0;

            public string Sprite;
            public string Material;
            public string Color;
            public Image.Type ImageType;
            public string Png;
            public int ItemId;
            public ulong SkinId;
            public float FadeIn;

            public void Serialize(IUiBuilder builder)
            {
                if (Sprite == default(string))
                    Sprite = DefaultSprite;

                if (Material == default(string))
                    Material = DefaultMaterial;

                if (Color == default(string))
                    Color = DefaultColor;

                if (ImageType == default(Image.Type))
                    ImageType = DefaultImageType;

                builder.StartComponent();
                builder.AddField("type", Type);

                if (Sprite != DefaultSprite)
                    builder.AddField("sprite", Sprite);

                if (Material != DefaultMaterial)
                    builder.AddField("material", Material);

                if (Color != DefaultColor)
                    builder.AddField("color", Color);

                if (ImageType != DefaultImageType)
                    builder.AddField("imagetype", builder.StringCache.Get(ImageType));

                if (Png != DefaultPng)
                    builder.AddField("png", Png);

                if (ItemId != DefaultItemId)
                    builder.AddField("itemid", ItemId);

                if (SkinId != DefaultSkinId)
                    builder.AddField("skinid", SkinId);

                if (FadeIn != DefaultFadeIn)
                    builder.AddField("fadeIn", FadeIn);

                builder.EndComponent();
            }
        }

        private struct UiRawImageComponent : IUiComponent
        {
            private const string Type = "UnityEngine.UI.RawImage";

            private const string DefaultSprite = "Assets/Icons/rust.png";
            private const string DefaultColor = "1 1 1 1";
            private const string DefaultMaterial = null;
            private const string DefaultUrl = null;
            private const string DefaultPng = null;
            private const float DefaultFadeIn = 0;

            public string Sprite;
            public string Color;
            public string Material;
            public string Url;
            public string Png;
            public float FadeIn;

            public void Serialize(IUiBuilder builder)
            {
                if (Sprite == default(string))
                    Sprite = DefaultSprite;

                if (Color == default(string))
                    Color = DefaultColor;

                builder.StartComponent();
                builder.AddField("type", Type);

                if (Sprite != DefaultSprite)
                    builder.AddField("sprite", Sprite);

                if (Color != DefaultColor)
                    builder.AddField("color", Color);

                if (Material != DefaultMaterial)
                    builder.AddField("material", Material);

                if (Url != DefaultUrl)
                    builder.AddField("url", Url);

                if (Png != DefaultPng)
                    builder.AddField("png", Png);

                if (FadeIn != DefaultFadeIn)
                    builder.AddField("fadeIn", FadeIn);

                builder.EndComponent();
            }
        }

        private struct UiRectTransformComponent : IUiComponent
        {
            private const string Type = "RectTransform";

            public const string DefaultAnchorMin = "0.0 0.0";
            public const string DefaultAnchorMax = "1.0 1.0";
            public const string DefaultOffsetMin = "0.0 0.0";
            public const string DefaultOffsetMax = "1.0 1.0";

            public string AnchorMin;
            public string AnchorMax;
            public string OffsetMin;
            public string OffsetMax;

            public void Serialize(IUiBuilder builder)
            {
                if (AnchorMin == default(string))
                    AnchorMin = DefaultAnchorMin;

                if (AnchorMax == default(string))
                    AnchorMax = DefaultAnchorMax;

                if (OffsetMin == default(string))
                    OffsetMin = DefaultOffsetMin;

                if (OffsetMax == default(string))
                    OffsetMax = DefaultOffsetMax;

                builder.StartComponent();
                builder.AddField("type", Type);

                if (AnchorMin != DefaultAnchorMin)
                    builder.AddField("anchormin", AnchorMin);

                if (AnchorMax != DefaultAnchorMax)
                    builder.AddField("anchormax", AnchorMax);

                if (OffsetMin != DefaultOffsetMin)
                    builder.AddField("offsetmin", OffsetMin);

                if (OffsetMax != DefaultOffsetMax)
                    builder.AddField("offsetmax", OffsetMax);

                builder.EndComponent();
            }
        }

        private struct UiTextComponent : IUiComponent
        {
            private const string Type = "UnityEngine.UI.Text";

            private const string DefaultText = "Text";
            private const int DefaultFontSize = 14;
            private const string DefaultFont = "RobotoCondensed-Bold.ttf";
            private const TextAnchor DefaultTextAlign = TextAnchor.UpperLeft;
            private const string DefaultColor = "1 1 1 1";
            private const VerticalWrapMode DefaultVerticalWrapMode = VerticalWrapMode.Truncate;
            private const float DefaultFadeIn = 0;

            public string Text;
            public int FontSize;
            public string Font;
            public TextAnchor TextAlign;
            public string Color;
            public VerticalWrapMode VerticalWrapMode;
            public float FadeIn;

            public void Serialize(IUiBuilder builder)
            {
                if (Text == default(string))
                    Text = DefaultText;

                if (FontSize == default(int))
                    FontSize = DefaultFontSize;

                if (Font == default(string))
                    Font = DefaultFont;

                if (TextAlign == default(TextAnchor))
                    TextAlign = DefaultTextAlign;

                if (Color == default(string))
                    Color = DefaultColor;

                if (VerticalWrapMode == default(VerticalWrapMode))
                    VerticalWrapMode = DefaultVerticalWrapMode;

                builder.StartComponent();
                builder.AddField("type", Type);

                if (Text != DefaultText)
                    builder.AddField("text", Text);

                if (FontSize != DefaultFontSize)
                    builder.AddField("fontSize", FontSize);

                if (Font != DefaultFont)
                    builder.AddField("font", Font);

                if (TextAlign != DefaultTextAlign)
                    builder.AddField("align", builder.StringCache.Get(TextAlign));

                if (Color != DefaultColor)
                    builder.AddField("color", Color);

                if (VerticalWrapMode != DefaultVerticalWrapMode)
                    builder.AddField("verticalOverflow", builder.StringCache.Get(VerticalWrapMode));

                if (FadeIn != DefaultFadeIn)
                    builder.AddField("fadeIn", FadeIn);

                builder.EndComponent();
            }
        }

        // Custom component for handling positions.
        private struct UiRectComponent : IUiComponent
        {
            private const string Type = "RectTransform";

            public const string DefaultAnchorMin = "0.0 0.0";

            private const string DefaultAnchor = "0 0";

            public UiRect Rect;

            public UiRectComponent(UiRect rect)
            {
                Rect = rect;
            }

            public UiRectComponent(float x, float y, string anchor = DefaultAnchor)
            {
                Rect = new UiRect
                {
                    Anchor = anchor,
                    XMin = x,
                    XMax = x,
                    YMin = y,
                    YMax = y
                };
            }

            public void Serialize(IUiBuilder builder)
            {
                builder.StartComponent();
                builder.AddField("type", Type);

                if (Rect.Anchor != DefaultAnchorMin)
                {
                    builder.AddField("anchormin", Rect.Anchor);
                    builder.AddField("anchormax", Rect.Anchor);
                }

                builder.AddXY("offsetmin", Rect.XMin, Rect.YMin);
                builder.AddXY("offsetmax", Rect.XMax, Rect.YMax);

                builder.EndComponent();
            }
        }

        #endregion

        #region UI Elements

        private interface IUiComponentCollection : IUiSerializable {}

        private struct UiComponents<T1> : IUiComponentCollection, IEnumerable<IUiComponentCollection>
            where T1 : IUiComponent
        {
            public T1 Component1;

            public void Add(T1 item) => Component1 = item;

            public void Serialize(IUiBuilder builder)
            {
                Component1.Serialize(builder);
            }

            public IEnumerator<IUiComponentCollection> GetEnumerator()
            {
                throw new NotImplementedException();
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                throw new NotImplementedException();
            }
        }

        private struct UiComponents<T1, T2> : IUiComponentCollection, IEnumerable<IUiComponentCollection>
            where T1 : IUiComponent
            where T2 : IUiComponent
        {
            public T1 Component1;
            public T2 Component2;

            public void Add(T1 item) => Component1 = item;
            public void Add(T2 item) => Component2 = item;

            public void Serialize(IUiBuilder builder)
            {
                Component1.Serialize(builder);
                Component2.Serialize(builder);
            }

            public IEnumerator<IUiComponentCollection> GetEnumerator()
            {
                throw new NotImplementedException();
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                throw new NotImplementedException();
            }
        }

        private struct UiElement<T> : IUiSerializable
            where T : IUiComponentCollection
        {
            public string Name;
            public string Parent;
            public string DestroyName;
            public float FadeOut;
            public T Components;

            public void Serialize(IUiBuilder builder)
            {
                builder.StartElement();
                builder.AddField("name", Name);
                builder.AddField("parent", Parent);

                if (DestroyName != default(string))
                    builder.AddField("destroyUi", DestroyName);

                if (FadeOut != default(float))
                    builder.AddField("fadeOut", FadeOut);

                builder.AddComponents(Components);
                builder.EndElement();
            }
        }

        private struct UiButtonElement<TButton, TText> : IUiSerializable
            where TButton : IUiComponentCollection
            where TText : IUiComponentCollection
        {
            public string Name;
            public string Parent;
            public string DestroyName;
            public float FadeOut;
            public TButton Button;
            public TText Text;

            public void Serialize(IUiBuilder builder)
            {
                builder.AddSerializable(new UiElement<TButton>
                {
                    Parent = Parent,
                    Name = Name,
                    Components = Button,
                    DestroyName = DestroyName,
                    FadeOut = FadeOut
                });

                builder.AddSerializable(new UiElement<TText>
                {
                    Parent = Name,
                    Components = Text,
                    FadeOut = FadeOut
                });
            }
        }

        #endregion

        #region UI

        private static class ContainerUi
        {
            public const float BaseOffsetY = 112;
            public const float BaseOffsetX = 192.5f;
            public const float HeaderWidth = 380;
            public const float HeaderHeight = 23;
            public const float PerRowOffsetY = 62;

            private const float PageButtonSpacing = 6;
            private const float PageButtonSize = HeaderHeight;

            private const string BlueButtonColor = "0.25 0.5 0.75 1";
            private const string BlueButtonTextColor = "0.75 0.85 1 1";
            private const string GreenButtonColor = "0.451 0.553 0.271 1";
            private const string GreenButtonTextColor = "0.659 0.918 0.2 1";

            private const string Name = "Backpacks.Container";

            public static void CreateContainerUi(BasePlayer player, int numPages, int activePageIndex, int capacity, Backpack backpack)
            {
                var numRows = 1 + (capacity - 1) / 6;
                var offsetY = BaseOffsetY + numRows * PerRowOffsetY;

                var builder = UiBuilder.Default;
                builder.Start();

                builder.AddSerializable(new UiElement<UiComponents<UiRectComponent>>
                {
                    Parent = "Hud.Menu",
                    Name = Name,
                    DestroyName = Name,
                    Components =
                    {
                        new UiRectComponent(BaseOffsetX, offsetY, Layout.AnchorBottomCenter),
                    }
                });

                var buttonLayoutProvider = new StatefulLayoutProvider
                {
                    Options = Layout.Option.AnchorBottom,
                    Spacing = 6
                };

                if (backpack.CanGather)
                {
                    AddGatherModeButton(builder, ref buttonLayoutProvider, player, backpack, activePageIndex);
                }

                if (backpack.CanRetrieve)
                {
                    AddRetrieveButton(builder, ref buttonLayoutProvider, player, backpack, activePageIndex);
                }

                if (numPages > 1)
                {
                    AddPaginationUi(builder, backpack, numPages, activePageIndex);
                }

                builder.End();
                builder.AddUi(player);
            }

            public static void DestroyUi(BasePlayer player)
            {
                CuiHelper.DestroyUi(player, Name);
            }

            private static void AddGatherModeButton(UiBuilder builder, ref StatefulLayoutProvider layoutProvider, BasePlayer player, Backpack backpack, int activePageIndex)
            {
                var gatherMode = backpack.GetGatherModeForPage(activePageIndex);

                builder.AddSerializable(new UiButtonElement<UiComponents<UiRectComponent, UiButtonComponent>, UiComponents<UiTextComponent>>
                {
                    Parent = Name,
                    Name = "Backpacks.Container.Gather",
                    Button =
                    {
                        new UiRectComponent(layoutProvider.Next(105, HeaderHeight)),
                        new UiButtonComponent
                        {
                            Command = "backpack.togglegather",
                            Color = gatherMode == GatherMode.None ? GreenButtonColor : BlueButtonColor
                        }
                    },
                    Text =
                    {
                        new UiTextComponent
                        {
                            Text = backpack.Plugin.GetMessage(player, gatherMode == GatherMode.All
                                ? "UI - Gather All"
                                : gatherMode == GatherMode.Existing
                                    ? "UI - Gather Existing"
                                    : "UI - Gather Off"),
                            Color = gatherMode == GatherMode.None ? GreenButtonTextColor : BlueButtonTextColor,
                            TextAlign = TextAnchor.MiddleCenter,
                            FontSize = 12
                        }
                    }
                });
            }

            private static void AddRetrieveButton(UiBuilder builder, ref StatefulLayoutProvider layoutProvider, BasePlayer player, Backpack backpack, int activePageIndex)
            {
                var retrieve = backpack.IsRetrievingFromPage(activePageIndex);

                builder.AddSerializable(new UiButtonElement<UiComponents<UiRectComponent, UiButtonComponent>, UiComponents<UiTextComponent>>
                {
                    Parent = Name,
                    Name = "Backpacks.Container.Retrieve",
                    Button =
                    {
                        new UiRectComponent(layoutProvider.Next(85, HeaderHeight)),
                        new UiButtonComponent
                        {
                            Command = "backpack.toggleretrieve",
                            Color = retrieve ? BlueButtonColor : GreenButtonColor
                        }
                    },
                    Text =
                    {
                        new UiTextComponent
                        {
                            Text = backpack.Plugin.GetMessage(player, retrieve
                                ? "UI - Retrieve On"
                                : "UI - Retrieve Off"),
                            Color = retrieve ? BlueButtonTextColor : GreenButtonTextColor,
                            TextAlign = TextAnchor.MiddleCenter,
                            FontSize = 12
                        }
                    }
                });
            }

            private static void AddPaginationUi(UiBuilder builder, Backpack backpack, int numPages, int activePageIndex)
            {
                var offsetY = backpack.Plugin._config.ContainerUi.ShowPageButtonsOnContainerBar
                    ? 0
                    : HeaderHeight + PageButtonSpacing;

                var buttonLayoutProvider = new StatelessLayoutProvider
                {
                    Options = Layout.Option.AnchorBottom | Layout.Option.AnchorRight,
                    Offset = new Vector2(-HeaderWidth, offsetY),
                    Size = new Vector2(PageButtonSize, PageButtonSize),
                    Spacing = PageButtonSpacing
                };

                for (var i = 0; i < numPages; i++)
                {
                    var visiblePageNumber = numPages - i;
                    var pageIndex = visiblePageNumber - 1;
                    var isActivePage = activePageIndex == visiblePageNumber - 1;

                    var name = DefaultStringCache.Instance.Get(i, n => $"{Name}.{n.ToString()}");

                    var buttonColor = isActivePage ? BlueButtonColor : GreenButtonColor;
                    var buttonTextColor = isActivePage ? BlueButtonTextColor : GreenButtonTextColor;

                    builder.AddSerializable(new UiButtonElement<UiComponents<UiRectComponent, UiButtonComponent>, UiComponents<UiTextComponent>>
                    {
                        Parent = Name,
                        Name = name,
                        Button =
                        {
                            new UiRectComponent(buttonLayoutProvider[i]),
                            new UiButtonComponent
                            {
                                Color = buttonColor,
                                Command = isActivePage ? "" : DefaultStringCache.Instance.Get(visiblePageNumber, n => $"backpack.open {n.ToString()}"),
                            }
                        },
                        Text =
                        {
                            new UiTextComponent
                            {
                                Text = DefaultStringCache.Instance.Get(visiblePageNumber),
                                TextAlign = TextAnchor.MiddleCenter,
                                Color = buttonTextColor
                            }
                        }
                    });

                    var arrowSize = new Vector2(PageButtonSize / 2, PageButtonSize / 2);
                    var arrowOffset = new Vector2(0, 1);

                    if (backpack.CanGather && backpack.GetGatherModeForPage(pageIndex) != GatherMode.None)
                    {
                        builder.AddSerializable(new UiElement<UiComponents<UiRectComponent, UiTextComponent>>
                        {
                            Parent = name,
                            Components =
                            {
                                new UiRectComponent(StatelessLayoutProvider.GetRect(0, Layout.Option.AnchorBottom | Layout.Option.AnchorRight | Layout.Option.Vertical, arrowSize, offset: arrowOffset)),
                                new UiTextComponent
                                {
                                    Text = "↓",
                                    FontSize = 10,
                                    TextAlign = TextAnchor.LowerRight,
                                    Color = buttonTextColor,
                                    VerticalWrapMode = VerticalWrapMode.Overflow
                                }
                            }
                        });
                    }

                    if (backpack.CanRetrieve && backpack.IsRetrievingFromPage(pageIndex))
                    {
                        builder.AddSerializable(new UiElement<UiComponents<UiRectComponent, UiTextComponent>>
                        {
                            Parent = name,
                            Components =
                            {
                                new UiRectComponent(StatelessLayoutProvider.GetRect(0, Layout.Option.AnchorRight | Layout.Option.Vertical, arrowSize, offset: -arrowOffset)),
                                new UiTextComponent
                                {
                                    Text = "↑",
                                    FontSize = 10,
                                    TextAlign = TextAnchor.UpperRight,
                                    Color = buttonTextColor,
                                    VerticalWrapMode = VerticalWrapMode.Overflow
                                }
                            }
                        });
                    }
                }
            }
        }

        private static class ButtonUi
        {
            private const string Name = "BackpacksUI";

            public static string CreateButtonUi(Configuration config)
            {
                var uiBuilder = UiBuilder.Default;

                uiBuilder.Start();
                uiBuilder.AddSerializable(new UiElement<UiComponents<UiRawImageComponent, UiRectTransformComponent>>
                {
                    Name = Name,
                    DestroyName = Name,
                    Parent = "Hud.Menu",
                    Components =
                    {
                        new UiRawImageComponent
                        {
                            Color = config.GUI.Color,
                            Sprite = "assets/content/ui/ui.background.tiletex.psd",
                        },
                        new UiRectTransformComponent
                        {
                            AnchorMin = config.GUI.GUIButtonPosition.AnchorsMin,
                            AnchorMax = config.GUI.GUIButtonPosition.AnchorsMax,
                            OffsetMin = config.GUI.GUIButtonPosition.OffsetsMin,
                            OffsetMax = config.GUI.GUIButtonPosition.OffsetsMax
                        },
                    }
                });

                var rectTransformComponent = new UiRectTransformComponent
                {
                    AnchorMin = "0 0",
                    AnchorMax = "1 1",
                };

                if (config.GUI.SkinId != 0)
                {
                    uiBuilder.AddSerializable(new UiElement<UiComponents<UiImageComponent, UiRectTransformComponent>>
                    {
                        Parent = Name,
                        Components =
                        {
                            new UiImageComponent
                            {
                                ItemId = SaddleBagItemId,
                                SkinId = config.GUI.SkinId
                            },
                            rectTransformComponent
                        }
                    });
                }
                else
                {
                    uiBuilder.AddSerializable(new UiElement<UiComponents<UiRawImageComponent, UiRectTransformComponent>>
                    {
                        Parent = Name,
                        Components =
                        {
                            new UiRawImageComponent
                            {
                                Url = config.GUI.Image
                            },
                            rectTransformComponent
                        }
                    });
                }

                uiBuilder.AddSerializable(new UiElement<UiComponents<UiButtonComponent, UiRectTransformComponent>>
                {
                    Parent = Name,
                    Components =
                    {
                        new UiButtonComponent
                        {
                            Command = "backpack.open",
                            Color = "0 0 0 0"
                        },
                        new UiRectTransformComponent
                        {
                            AnchorMin = "0 0",
                            AnchorMax = "1 1",
                        }
                    }
                });

                uiBuilder.End();
                return uiBuilder.ToJson();
            }

            public static void DestroyUi(BasePlayer player)
            {
                CuiHelper.DestroyUi(player, Name);
            }
        }

        #endregion

        #region Subscriber Manager

        private class EventSubscriber
        {
            public static EventSubscriber FromSpec(Plugin plugin, Dictionary<string, object> spec)
            {
                var subscriber = new EventSubscriber { Plugin = plugin };

                GetOption(spec, nameof(OnBackpackLoaded), out subscriber.OnBackpackLoaded);
                GetOption(spec, nameof(OnBackpackItemCountChanged), out subscriber.OnBackpackItemCountChanged);
                GetOption(spec, nameof(OnBackpackGatherChanged), out subscriber.OnBackpackGatherChanged);
                GetOption(spec, nameof(OnBackpackRetrieveChanged), out subscriber.OnBackpackRetrieveChanged);

                return subscriber;
            }

            private static void GetOption<T>(Dictionary<string, object> dict, string key, out T result)
            {
                object value;
                result = dict.TryGetValue(key, out value) && value is T
                    ? (T)value
                    : default(T);
            }

            public Plugin Plugin { get; private set; }
            public Action<BasePlayer, int, int> OnBackpackLoaded;
            public Action<BasePlayer, int, int> OnBackpackItemCountChanged;
            public Action<BasePlayer, bool> OnBackpackGatherChanged;
            public Action<BasePlayer, bool> OnBackpackRetrieveChanged;
        }

        private class SubscriberManager
        {
            private readonly Dictionary<string, EventSubscriber> _subscribers = new Dictionary<string, EventSubscriber>();

            public void AddSubscriber(Plugin plugin, Dictionary<string, object> spec)
            {
                RemoveSubscriber(plugin);

                _subscribers[plugin.Name] = EventSubscriber.FromSpec(plugin, spec);
            }

            public void RemoveSubscriber(Plugin plugin)
            {
                _subscribers.Remove(plugin.Name);
            }

            public void BroadcastBackpackLoaded(Backpack backpack)
            {
                if (_subscribers.Count == 0 || (object)backpack.Owner == null)
                    return;

                foreach (var subscriber in _subscribers.Values)
                {
                    subscriber.OnBackpackLoaded?.Invoke(backpack.Owner, backpack.ItemCount, backpack.Capacity);
                }
            }

            public void BroadcastItemCountChanged(Backpack backpack)
            {
                if (_subscribers.Count == 0 || (object)backpack.Owner == null)
                    return;

                foreach (var subscriber in _subscribers.Values)
                {
                    subscriber.OnBackpackItemCountChanged?.Invoke(backpack.Owner, backpack.ItemCount, backpack.Capacity);
                }
            }

            public void BroadcastGatherChanged(Backpack backpack, bool isGathering)
            {
                if (_subscribers.Count == 0 || (object)backpack.Owner == null)
                    return;

                foreach (var subscriber in _subscribers.Values)
                {
                    subscriber.OnBackpackGatherChanged?.Invoke(backpack.Owner, isGathering);
                }
            }

            public void BroadcastRetrieveChanged(Backpack backpack, bool isRetrieving)
            {
                if (_subscribers.Count == 0 || (object)backpack.Owner == null)
                    return;

                foreach (var subscriber in _subscribers.Values)
                {
                    subscriber.OnBackpackRetrieveChanged?.Invoke(backpack.Owner, isRetrieving);
                }
            }
        }

        #endregion

        #region Backpack Capacity Manager

        private class BackpackCapacityManager
        {
            private class BackpackSize
            {
                public readonly int Capacity;
                public readonly string Permission;

                public BackpackSize(int capacity, string permission)
                {
                    Capacity = capacity;
                    Permission = permission;
                }
            }

            private readonly Backpacks _plugin;
            private Configuration _config;
            private BackpackSize[] _sortedBackpackSizes;
            private readonly Dictionary<ulong, int> _cachedPlayerBackpackSizes = new Dictionary<ulong, int>();

            public BackpackCapacityManager(Backpacks plugin)
            {
                _plugin = plugin;
            }

            public void Init(Configuration config)
            {
                _config = config;

                var backpackSizeList = new List<BackpackSize>();

                if (config.BackpackSize.EnableLegacyRowPermissions)
                {
                    for (var row = MinRows; row <= MaxRows; row++)
                    {
                        var backpackSize = new BackpackSize(row * SlotsPerRow, $"{UsagePermission}.{row.ToString()}");
                        _plugin.permission.RegisterPermission(backpackSize.Permission, _plugin);
                        backpackSizeList.Add(backpackSize);
                    }
                }

                foreach (var capacity in new HashSet<int>(config.BackpackSize.PermissionSizes))
                {
                    backpackSizeList.Add(new BackpackSize(capacity, $"{SizePermission}.{capacity.ToString()}"));
                }

                backpackSizeList.Sort((a, b) => a.Capacity.CompareTo(b.Capacity));
                _sortedBackpackSizes = backpackSizeList.ToArray();

                foreach (var backpackSize in _sortedBackpackSizes)
                {
                    // The "backpacks.use.X" perms are registered all at once to make them easier to view.
                    if (backpackSize.Permission.StartsWith(UsagePermission))
                        continue;

                    _plugin.permission.RegisterPermission(backpackSize.Permission, _plugin);
                }
            }

            public void ForgetCachedCapacity(ulong userId)
            {
                _cachedPlayerBackpackSizes.Remove(userId);
            }

            public int GetCapacity(ulong userId, string userIdString)
            {
                int capacity;
                if (_cachedPlayerBackpackSizes.TryGetValue(userId, out capacity))
                    return capacity;

                capacity = DetermineCapacityFromPermission(userIdString);
                _cachedPlayerBackpackSizes[userId] = capacity;
                return capacity;
            }

            private int DetermineCapacityFromPermission(string userIdString)
            {
                if (!_plugin.permission.UserHasPermission(userIdString, UsagePermission))
                    return 0;

                for (var i = _sortedBackpackSizes.Length - 1; i >= 0; i--)
                {
                    var backpackSize = _sortedBackpackSizes[i];
                    if (_plugin.permission.UserHasPermission(userIdString, backpackSize.Permission))
                        return backpackSize.Capacity;
                }

                return _config.BackpackSize.DefaultSize;
            }
        }

        #endregion

        #region Backpack Manager

        private class BackpackManager
        {
            private static string DetermineBackpackPath(ulong userId) => $"{nameof(Backpacks)}/{userId.ToString()}";

            private readonly Backpacks _plugin;

            private readonly Dictionary<ulong, Backpack> _cachedBackpacks = new Dictionary<ulong, Backpack>();
            private readonly Dictionary<ulong, string> _backpackPathCache = new Dictionary<ulong, string>();
            private readonly Dictionary<ItemContainer, Backpack> _backpackContainers = new Dictionary<ItemContainer, Backpack>();

            private readonly List<Backpack> _tempBackpackList = new List<Backpack>(PoolUtils.BackpackPoolSize);

            public BackpackManager(Backpacks plugin)
            {
                _plugin = plugin;
            }

            public void DiscoverBags(Plugin bagOfHolding)
            {
                foreach (var backpack in _cachedBackpacks.Values)
                {
                    backpack.DiscoverBags(bagOfHolding);
                }
            }

            public void HandleCapacityPermissionChangedForGroup(string groupName)
            {
                foreach (var backpack in _cachedBackpacks.Values)
                {
                    if (!_plugin.permission.UserHasGroup(backpack.OwnerIdString, groupName))
                        continue;

                    _plugin._backpackCapacityManager.ForgetCachedCapacity(backpack.OwnerId);
                    backpack.SetFlag(Backpack.Flag.CapacityCached, false);
                }
            }

            public void HandleCapacityPermissionChangedForUser(string userIdString)
            {
                var backpack = GetBackpackIfCached(userIdString);
                if (backpack == null)
                    return;

                _plugin._backpackCapacityManager.ForgetCachedCapacity(backpack.OwnerId);
                backpack.SetFlag(Backpack.Flag.CapacityCached, false);
            }

            public void HandleRestrictionPermissionChangedForGroup(string groupName)
            {
                foreach (var backpack in _cachedBackpacks.Values)
                {
                    if (!_plugin.permission.UserHasGroup(backpack.OwnerIdString, groupName))
                        continue;

                    backpack.SetFlag(Backpack.Flag.RestrictionsCached, false);
                }
            }

            public void HandleRestrictionPermissionChangedForUser(string userIdString)
            {
                var backpack = GetBackpackIfCached(userIdString);
                if (backpack == null)
                    return;

                backpack.SetFlag(Backpack.Flag.RestrictionsCached, false);
            }

            public void HandleGatherPermissionChangedForGroup(string groupName)
            {
                foreach (var backpack in _cachedBackpacks.Values)
                {
                    if (!_plugin.permission.UserHasGroup(backpack.OwnerIdString, groupName))
                        continue;

                    backpack.SetFlag(Backpack.Flag.GatherCached, false);
                }
            }

            public void HandleGatherPermissionChangedForUser(string userIdString)
            {
                GetBackpackIfCached(userIdString)?.SetFlag(Backpack.Flag.GatherCached, false);
            }

            public void HandleRetrievePermissionChangedForGroup(string groupName)
            {
                foreach (var backpack in _cachedBackpacks.Values)
                {
                    if (!_plugin.permission.UserHasGroup(backpack.OwnerIdString, groupName))
                        continue;

                    backpack.SetFlag(Backpack.Flag.RetrieveCached, false);
                }
            }

            public void HandleRetrievePermissionChangedForUser(string userIdString)
            {
                GetBackpackIfCached(userIdString)?.SetFlag(Backpack.Flag.RetrieveCached, false);
            }

            public void HandleGroupChangeForUser(string userIdString)
            {
                var backpack = GetBackpackIfCached(userIdString);
                if (backpack == null)
                    return;

                _plugin._backpackCapacityManager.ForgetCachedCapacity(backpack.OwnerId);
                backpack.SetFlag(Backpack.Flag.CapacityCached, false);
                backpack.SetFlag(Backpack.Flag.RestrictionsCached, false);
                backpack.SetFlag(Backpack.Flag.GatherCached, false);
                backpack.SetFlag(Backpack.Flag.RetrieveCached, false);
            }

            public bool IsBackpack(ItemContainer container)
            {
                return _backpackContainers.ContainsKey(container);
            }

            public bool IsBackpack(ItemContainer container, out Backpack backpack, out int pageIndex)
            {
                if (!_backpackContainers.TryGetValue(container, out backpack))
                {
                    pageIndex = 0;
                    return false;
                }

                pageIndex = backpack.GetPageIndexForContainer(container);
                if (pageIndex == -1)
                {
                    pageIndex = 0;
                    return false;
                }

                return true;
            }

            public bool HasBackpackFile(ulong userId)
            {
                return Interface.Oxide.DataFileSystem.ExistsDatafile(GetBackpackPath(userId));
            }

            public Backpack GetBackpackIfCached(ulong userId)
            {
                Backpack backpack;
                return _cachedBackpacks.TryGetValue(userId, out backpack)
                    ? backpack
                    : null;
            }

            public Backpack GetBackpack(ulong userId)
            {
                return GetBackpackIfCached(userId) ?? Load(userId);
            }

            public Backpack GetBackpackIfExists(ulong userId)
            {
                return GetBackpackIfCached(userId) ?? (HasBackpackFile(userId)
                    ? Load(userId)
                    : null);
            }

            public void RegisterContainer(ItemContainer container, Backpack backpack)
            {
                _backpackContainers[container] = backpack;
            }

            public void UnregisterContainer(ItemContainer container)
            {
                _backpackContainers.Remove(container);
            }

            public Backpack GetCachedBackpackForContainer(ItemContainer container)
            {
                Backpack backpack;
                return _backpackContainers.TryGetValue(container, out backpack)
                    ? backpack
                    : null;
            }

            public Dictionary<ulong, ItemContainer> GetAllCachedContainers()
            {
                var cachedContainersByUserId = new Dictionary<ulong, ItemContainer>();

                foreach (var entry in _cachedBackpacks)
                {
                    var container = entry.Value.GetContainer();
                    if (container != null)
                        cachedContainersByUserId[entry.Key] = container;
                }

                return cachedContainersByUserId;
            }

            public DroppedItemContainer Drop(ulong userId, Vector3 position, List<DroppedItemContainer> collect = null)
            {
                return GetBackpackIfExists(userId)?.Drop(position, collect);
            }

            public bool TryOpenBackpack(BasePlayer looter, ulong backpackOwnerId)
            {
                if (backpackOwnerId == 0)
                {
                    backpackOwnerId = looter.userID;
                }

                return GetBackpack(backpackOwnerId).TryOpen(looter);
            }

            public bool TryOpenBackpackContainer(BasePlayer looter, ulong backpackOwnerId, ItemContainer container)
            {
                if (backpackOwnerId == 0)
                {
                    backpackOwnerId = looter.userID;
                }

                Backpack backpack;
                int pageIndex;
                if (!IsBackpack(container, out backpack, out pageIndex) || backpack.OwnerId != backpackOwnerId)
                {
                    backpack = GetBackpack(backpackOwnerId);
                    pageIndex = -1;
                }

                return backpack.TryOpen(looter, pageIndex);
            }

            public bool TryOpenBackpackPage(BasePlayer looter, ulong backpackOwnerId, int pageIndex = -1)
            {
                if (backpackOwnerId == 0)
                {
                    backpackOwnerId = looter.userID;
                }

                return GetBackpack(backpackOwnerId).TryOpen(looter, pageIndex);
            }

            public void ClearBackpackFile(ulong userId)
            {
                Interface.Oxide.DataFileSystem.WriteObject<object>(DetermineBackpackPath(userId), null);
            }

            public bool TryEraseForPlayer(ulong userId)
            {
                var backpack = GetBackpackIfExists(userId);
                if (backpack == null)
                    return false;

                backpack.EraseContents(force: true);
                return true;
            }

            public IEnumerator SaveAllAndKill(bool async, bool keepInUseBackpacks)
            {
                // Clear the list before usage, in case an error prevented cleanup, or in case coroutine was restarted.
                _tempBackpackList.Clear();

                // Copy the list of cached backpacks because it may be modified.
                foreach (var backpack in _cachedBackpacks.Values)
                {
                    _tempBackpackList.Add(backpack);
                }

                foreach (var backpack in _tempBackpackList)
                {
                    var didSave = backpack.SaveIfChanged();

                    // Kill the backpack to free up space, if no admins are viewing it and its owner is disconnected.
                    if (!keepInUseBackpacks || (!backpack.HasLooters && BasePlayer.FindByID(backpack.OwnerId) == null))
                    {
                        backpack.Kill();
                        _cachedBackpacks.Remove(backpack.OwnerId);
                        _backpackPathCache.Remove(backpack.OwnerId);
                        var backpackToFree = backpack;
                        Pool.Free(ref backpackToFree);
                    }

                    if (didSave && async)
                        yield return null;
                }

                _tempBackpackList.Clear();
            }

            public void ClearCache()
            {
                foreach (var backpack in _cachedBackpacks.Values)
                {
                    var backpackToFree = backpack;
                    Pool.Free(ref backpackToFree);
                }

                _cachedBackpacks.Clear();
            }

            private string GetBackpackPath(ulong userId)
            {
                string filepath;
                if (!_backpackPathCache.TryGetValue(userId, out filepath))
                {
                    filepath = DetermineBackpackPath(userId);
                    _backpackPathCache[userId] = filepath;
                }

                return filepath;
            }

            private Backpack Load(ulong userId)
            {
                #if DEBUG_BACKPACK_LIFECYCLE
                LogDebug($"Backpack::Load | {userId.ToString()}");
                #endif

                var filePath = GetBackpackPath(userId);

                Backpack backpack = null;

                var dataFile = Interface.Oxide.DataFileSystem.GetFile(filePath);
                if (dataFile.Exists())
                {
                    backpack = dataFile.ReadObject<Backpack>();
                }

                // Note: Even if the user has a backpack file, the file contents may be null in some edge cases.
                // For example, if a data file cleaner plugin writes the file content as `null`.
                if (backpack == null)
                {
                    backpack = Pool.Get<Backpack>();
                }

                backpack.Setup(_plugin, userId, dataFile);
                _cachedBackpacks[userId] = backpack;

                _plugin._subscriberManager.BroadcastBackpackLoaded(backpack);

                return backpack;
            }

            private Backpack GetBackpackIfCached(string userIdString)
            {
                ulong userId;
                if (!ulong.TryParse(userIdString, out userId))
                    return null;

                return GetBackpackIfCached(userId);
            }
        }

        #endregion

        #region Backpack Networking

        private class BackpackNetworkController
        {
            private const uint StartNetworkGroupId = 10000000;
            private static uint _nextNetworkGroupId = StartNetworkGroupId;

            public static void ResetNetworkGroupId()
            {
                _nextNetworkGroupId = StartNetworkGroupId;
            }

            public static bool IsBackpackNetworkGroup(Network.Visibility.Group group)
            {
                return group.ID >= StartNetworkGroupId && group.ID < _nextNetworkGroupId;
            }

            public static BackpackNetworkController Create()
            {
                return new BackpackNetworkController(_nextNetworkGroupId++);
            }

            public readonly Network.Visibility.Group NetworkGroup;

            private readonly List<BasePlayer> _subscribers = new List<BasePlayer>(1);

            private BackpackNetworkController(uint networkGroupId)
            {
                NetworkGroup = new Network.Visibility.Group(null, networkGroupId);
            }

            public void Subscribe(BasePlayer player)
            {
                if (player.Connection == null || _subscribers.Contains(player))
                    return;

                _subscribers.Add(player);

                // Send the client a message letting them know they are subscribed to the group.
                ServerMgr.OnEnterVisibility(player.Connection, NetworkGroup);

                // Send the client a snapshot of every entity currently in the group.
                // Don't use the entity queue for this because it could be cleared which could cause updates to be missed.
                foreach (var networkable in NetworkGroup.networkables)
                {
                    (networkable.handler as BaseNetworkable).SendAsSnapshot(player.Connection);
                }

                if (!NetworkGroup.subscribers.Contains(player.Connection))
                {
                    // Register the client with the group so that entities added to it will be automatically sent to the client.
                    NetworkGroup.subscribers.Add(player.Connection);
                }

                var subscriber = player.net.subscriber;
                if (!subscriber.subscribed.Contains(NetworkGroup))
                {
                    // Register the group with the client so that ShouldNetworkTo() returns true in SendNetworkUpdate().
                    // This covers cases such as toggling a pager's silent mode.
                    subscriber.subscribed.Add(NetworkGroup);
                }
            }

            public void Unsubscribe(BasePlayer player)
            {
                if (!_subscribers.Remove(player))
                    return;

                if (player.Connection == null)
                    return;

                // Unregister the client from the group so they don't get future entity updates.
                NetworkGroup.subscribers.Remove(player.Connection);
                player.net.subscriber.subscribed.Remove(NetworkGroup);

                // Send the client a message so they kill all client-side entities in the group.
                ServerMgr.OnLeaveVisibility(player.Connection, NetworkGroup);
            }

            public void UnsubscribeAll()
            {
                for (var i = _subscribers.Count - 1; i >= 0; i--)
                {
                    Unsubscribe(_subscribers[i]);
                }
            }
        }

        #endregion

        #region Unity Components

        private class NoRagdollCollision : FacepunchBehaviour
        {
            private Collider _collider;

            private void Awake()
            {
                _collider = GetComponent<Collider>();
            }

            private void OnCollisionEnter(Collision collision)
            {
                if (collision.collider.IsOnLayer(Rust.Layer.Ragdoll))
                {
                    Physics.IgnoreCollision(_collider, collision.collider);
                }
            }
        }

        private class BackpackCloseListener : EntityComponent<StorageContainer>
        {
            public static void AddToBackpackStorage(Backpacks plugin, StorageContainer containerEntity, Backpack backpack)
            {
                var component = containerEntity.gameObject.AddComponent<BackpackCloseListener>();
                component._plugin = plugin;
                component._backpack = backpack;
            }

            private Backpacks _plugin;
            private Backpack _backpack;

            // Called via `entity.SendMessage("PlayerStoppedLooting", player)` in PlayerLoot.Clear().
            private void PlayerStoppedLooting(BasePlayer looter)
            {
                _plugin.TrackStart();
                _backpack.OnClosed(looter);
                ExposedHooks.OnBackpackClosed(looter, _backpack.OwnerId, looter.inventory.loot.containers.FirstOrDefault());
                _plugin.TrackEnd();
            }
        }

        #endregion

        #region Item Query

        private struct ItemQuery
        {
            public static ItemQuery FromItem(Item item)
            {
                return new ItemQuery
                {
                    BlueprintId = item.blueprintTarget,
                    DataInt = item.instanceData?.dataInt ?? 0,
                    DisplayName = item.name,
                    ItemDefinition = item.info,
                    ItemId = item.info.itemid,
                    SkinId = item.skin,
                };
            }

            public static ItemQuery Parse(Dictionary<string, object> raw)
            {
                var itemQuery = new ItemQuery();

                GetOption(raw, "BlueprintId", out itemQuery.BlueprintId);
                GetOption(raw, "DisplayName", out itemQuery.DisplayName);
                GetOption(raw, "DataInt", out itemQuery.DataInt);
                GetOption(raw, "FlagsContain", out itemQuery.FlagsContain);
                GetOption(raw, "FlagsEqual", out itemQuery.FlagsEqual);
                GetOption(raw, "ItemDefinition", out itemQuery.ItemDefinition);
                GetOption(raw, "ItemId", out itemQuery.ItemId);
                GetOption(raw, "MinCondition", out itemQuery.MinCondition);
                GetOption(raw, "RequireEmpty", out itemQuery.RequireEmpty);
                GetOption(raw, "SkinId", out itemQuery.SkinId);

                return itemQuery;
            }

            private static void GetOption<T>(Dictionary<string, object> dict, string key, out T result)
            {
                object value;
                result = dict.TryGetValue(key, out value) && value is T
                    ? (T)value
                    : default(T);
            }

            public int? BlueprintId;
            public int? DataInt;
            public string DisplayName;
            public Item.Flag? FlagsContain;
            public Item.Flag? FlagsEqual;
            public ItemDefinition ItemDefinition;
            public int? ItemId;
            public float MinCondition;
            public bool RequireEmpty;
            public ulong? SkinId;

            private int? GetItemId()
            {
                if (ItemDefinition != null)
                    return ItemDefinition?.itemid ?? ItemId;

                return ItemId;
            }

            private ItemDefinition GetItemDefinition()
            {
                if ((object)ItemDefinition == null && ItemId.HasValue)
                {
                    ItemDefinition = ItemManager.FindItemDefinition(ItemId.Value);
                }

                return ItemDefinition;
            }

            private bool HasCondition()
            {
                return GetItemDefinition()?.condition.enabled ?? false;
            }

            private float ConditionNormalized(ItemData itemData)
            {
                return itemData.Condition / itemData.MaxCondition;
            }

            private float MaxConditionNormalized(ItemData itemData)
            {
                var itemDefinition = GetItemDefinition();
                if (itemDefinition == null)
                    return 1;

                return itemData.MaxCondition / itemDefinition.condition.max;
            }

            public int GetUsableAmount(Item item)
            {
                var itemId = GetItemId();
                if (itemId.HasValue && itemId != item.info.itemid)
                    return 0;

                if (SkinId.HasValue && SkinId != item.skin)
                    return 0;

                if (BlueprintId.HasValue && BlueprintId != item.blueprintTarget)
                    return 0;

                if (DataInt.HasValue && DataInt != (item.instanceData?.dataInt ?? 0))
                    return 0;

                if (FlagsContain.HasValue && !item.flags.HasFlag(FlagsContain.Value))
                    return 0;

                if (FlagsEqual.HasValue && FlagsEqual != item.flags)
                    return 0;

                if (MinCondition > 0 && HasCondition() && (item.conditionNormalized < MinCondition || item.maxConditionNormalized < MinCondition))
                    return 0;

                if (!string.IsNullOrEmpty(DisplayName) && !StringUtils.Equals(DisplayName, item.name))
                    return 0;

                return RequireEmpty && item.contents?.itemList?.Count > 0
                    ? Math.Max(0, item.amount - 1)
                    : item.amount;
            }

            public int GetUsableAmount(ItemData itemData)
            {
                var itemId = GetItemId();
                if (itemId.HasValue && itemId != itemData.ID)
                    return 0;

                if (SkinId.HasValue && SkinId != itemData.Skin)
                    return 0;

                if (BlueprintId.HasValue && BlueprintId != itemData.BlueprintTarget)
                    return 0;

                if (DataInt.HasValue && DataInt != itemData.DataInt)
                    return 0;

                if (FlagsContain.HasValue && !itemData.Flags.HasFlag(FlagsContain.Value))
                    return 0;

                if (FlagsEqual.HasValue && FlagsEqual != itemData.Flags)
                    return 0;

                if (MinCondition > 0 && HasCondition() && (ConditionNormalized(itemData) < MinCondition || MaxConditionNormalized(itemData) < MinCondition))
                    return 0;

                if (!string.IsNullOrEmpty(DisplayName) && !StringUtils.Equals(DisplayName, itemData.Name))
                    return 0;

                return RequireEmpty && itemData.Contents?.Count > 0
                    ? Math.Max(0, itemData.Amount - 1)
                    : itemData.Amount;
            }
        }

        #endregion

        #region Container Adapters

        private struct WipeContext
        {
            public int SlotsKept;
        }

        private interface IContainerAdapter : Pool.IPooled
        {
            int PageIndex { get; }
            int Capacity { get; set; }
            int ItemCount { get; }
            bool HasItems { get; }
            int PositionOf(ref ItemQuery itemQuery);
            int CountItems(ref ItemQuery itemQuery);
            int SumItems(ref ItemQuery itemQuery);
            int TakeItems(ref ItemQuery itemQuery, int amount, List<Item> collect);
            bool TryDepositItem(Item item);
            void ReclaimFractionForSoftcore(float fraction, List<Item> collect);
            void TakeRestrictedItems(List<Item> collect);
            void TakeAllItems(List<Item> collect, int startPosition = 0);
            void SerializeForNetwork(List<ProtoBuf.Item> saveList);
            void SerializeTo(List<ItemData> saveList, List<ItemData> itemsToReleaseToPool);
            void EraseContents(WipeRuleset ruleset, ref WipeContext wipeContext);
            void Kill();
        }

        private class VirtualContainerAdapter : IContainerAdapter
        {
            public int PageIndex { get; private set; }
            public int Capacity { get; set; }
            public List<ItemData> ItemDataList { get; } = new List<ItemData>(_maxCapacityPerPage);
            public int ItemCount => ItemDataList.Count;
            public bool HasItems => ItemCount > 0;

            private Backpack _backpack;

            public VirtualContainerAdapter Setup(Backpack backpack, int pageIndex, int capacity)
            {
                #if DEBUG_POOLING
                LogDebug($"VirtualContainerAdapter::Setup | PageIndex: {pageIndex.ToString()} | Capacity: {capacity.ToString()}");
                #endif

                PageIndex = pageIndex;
                Capacity = capacity;
                _backpack = backpack;
                return this;
            }

            public void EnterPool()
            {
                #if DEBUG_POOLING
                LogDebug($"VirtualContainerAdapter::EnterPool | {PoolUtils.GetStats<VirtualContainerAdapter>()}");
                #endif

                PageIndex = 0;
                Capacity = 0;
                PoolUtils.ResetItemsAndClear(ItemDataList);
                _backpack = null;
            }

            public void LeavePool()
            {
                #if DEBUG_POOLING
                LogDebug($"VirtualContainerAdapter::LeavePool | {PoolUtils.GetStats<VirtualContainerAdapter>()}");
                #endif
            }

            public void SortByPosition()
            {
                ItemDataList.Sort((a, b) => a.Position.CompareTo(b.Position));
            }

            public int PositionOf(ref ItemQuery itemQuery)
            {
                SortByPosition();
                return ItemUtils.PositionOf(ItemDataList, ref itemQuery);
            }

            public int CountItems(ref ItemQuery itemQuery)
            {
                return ItemUtils.CountItems(ItemDataList, ref itemQuery);
            }

            public int SumItems(ref ItemQuery itemQuery)
            {
                return ItemUtils.SumItems(ItemDataList, ref itemQuery);
            }

            public int TakeItems(ref ItemQuery itemQuery, int amount, List<Item> collect)
            {
                var originalItemCount = ItemCount;

                var amountTaken = ItemUtils.TakeItems(ItemDataList, ref itemQuery, amount, collect);
                if (amountTaken > 0)
                {
                    _backpack.SetFlag(Backpack.Flag.Dirty, true);

                    if (ItemCount != originalItemCount)
                    {
                        _backpack.HandleItemCountChanged();
                    }
                }

                return amountTaken;
            }

            public void ReclaimFractionForSoftcore(float fraction, List<Item> collect)
            {
                // For some reason, the vanilla reclaim logic doesn't take the last item.
                if (ItemDataList.Count <= 1)
                    return;

                var numToTake = Mathf.Ceil(ItemDataList.Count * fraction);

                for (var i = 0; i < numToTake; i++)
                {
                    var indexToTake = UnityEngine.Random.Range(0, ItemDataList.Count);
                    var itemDataToTake = ItemDataList[indexToTake];
                    if (itemDataToTake.Amount > 1)
                    {
                        // Prefer taking a smaller stack if possible (vanilla behavior).
                        for (var j = 0; j < ItemDataList.Count; j++)
                        {
                            var alternateItemData = ItemDataList[j];
                            if (alternateItemData.ID != itemDataToTake.ID)
                                continue;

                            if (alternateItemData.Amount >= itemDataToTake.Amount)
                                continue;

                            itemDataToTake = alternateItemData;
                            indexToTake = j;
                        }
                    }

                    var item = itemDataToTake.ToItem();
                    if (item != null)
                    {
                        collect.Add(item);
                    }

                    RemoveItem(indexToTake);
                }
            }

            public void TakeRestrictedItems(List<Item> collect)
            {
                if (ItemDataList.Count == 0)
                    return;

                for (var i = ItemDataList.Count - 1; i >= 0; i--)
                {
                    var itemData = ItemDataList[i];
                    if (_backpack.RestrictionRuleset.AllowsItem(itemData))
                        continue;

                    var item = itemData.ToItem();
                    if (item != null)
                    {
                        collect.Add(item);
                    }

                    RemoveItem(i);
                }
            }

            public void TakeAllItems(List<Item> collect, int startPosition = 0)
            {
                SortByPosition();

                if (ItemDataList.Count == 0)
                    return;

                for (var i = 0; i < ItemDataList.Count; i++)
                {
                    var itemData = ItemDataList[i];
                    if (itemData.Position < startPosition)
                        continue;

                    var item = itemData.ToItem();
                    if (item != null)
                    {
                        collect.Add(item);
                    }

                    RemoveItem(i--);
                }
            }

            public void SerializeForNetwork(List<ProtoBuf.Item> saveList)
            {
                ItemUtils.SerializeForNetwork(ItemDataList, saveList);
            }

            public void SerializeTo(List<ItemData> saveList, List<ItemData> itemsToReleaseToPool)
            {
                foreach (var itemData in ItemDataList)
                {
                    saveList.Add(itemData);
                }
            }

            public void EraseContents(WipeRuleset ruleset, ref WipeContext wipeContext)
            {
                if (ruleset == null || ruleset.DisallowsAll)
                {
                    if (ItemDataList.Count > 0)
                    {
                        PoolUtils.ResetItemsAndClear(ItemDataList);
                        _backpack.SetFlag(Backpack.Flag.Dirty, true);
                    }
                    return;
                }

                SortByPosition();

                for (var i = 0; i < ItemDataList.Count; i++)
                {
                    var itemData = ItemDataList[i];
                    if ((ruleset.MaxSlotsToKeep < 0 || wipeContext.SlotsKept < ruleset.MaxSlotsToKeep)
                        && ruleset.AllowsItem(itemData))
                    {
                        wipeContext.SlotsKept++;
                        continue;
                    }

                    RemoveItem(i--);
                }
            }

            public void Kill()
            {
                // Intentionally not implemented because there are no actual resources to destroy.
            }

            public VirtualContainerAdapter CopyItemsFrom(List<ItemData> itemDataList)
            {
                var startPosition = PageIndex * _maxCapacityPerPage;
                var endPosition = startPosition + Capacity;

                // This assumes the list has already been sorted by item position.
                foreach (var itemData in itemDataList)
                {
                    if (itemData.Position < startPosition)
                        continue;

                    if (itemData.Position >= endPosition)
                        break;

                    ItemDataList.Add(itemData);
                }

                return this;
            }

            public bool TryDepositItem(Item item)
            {
                var firstEmptyPosition = GetFirstEmptyPosition();
                if (firstEmptyPosition >= Capacity)
                {
                    // To keep things simple, simply deny the item if there are no empty slots. This is done because
                    // it's difficult to know whether the item can be stacked with an existing item without calling
                    // stacking related hooks which require a physical page and item. This results in an edge case
                    // where if all pages are full, and no physical pages can accept the item, then any full virtual
                    // page would reject the item, even if upgrading the page would allow the item. In the future, the
                    // page could be upgraded to a physical container to handle this edge case if necessary.
                    return false;
                }

                if (!_backpack.ShouldAcceptItem(item, null))
                    return false;

                var itemData = Pool.Get<ItemData>().Setup(item, firstEmptyPosition);
                ItemDataList.Add(itemData);

                item.RemoveFromContainer();
                item.Remove();

                _backpack.SetFlag(Backpack.Flag.Dirty, true);
                _backpack.HandleItemCountChanged();
                return true;
            }

            private int GetFirstEmptyPosition()
            {
                var nextPossiblePosition = 0;

                for (var i = 0; i < ItemDataList.Count; i++)
                {
                    var itemData = ItemDataList[i];
                    if (itemData.Position > nextPossiblePosition)
                        return i;

                    nextPossiblePosition++;
                }

                return nextPossiblePosition;
            }

            private void RemoveItem(int index)
            {
                var itemData = ItemDataList[index];
                ItemDataList.RemoveAt(index);
                Pool.Free(ref itemData);
                _backpack.SetFlag(Backpack.Flag.Dirty, true);
                _backpack.HandleItemCountChanged();
            }
        }

        private class ItemContainerAdapter : IContainerAdapter
        {
            public int PageIndex { get; private set; }
            public int Capacity
            {
                get { return ItemContainer.capacity; }
                set { ItemContainer.capacity = value; }
            }
            public ItemContainer ItemContainer { get; private set; }
            public int ItemCount => ItemContainer.itemList.Count;
            public bool HasItems => ItemCount > 0;

            private Backpack _backpack;

            private Action _onDirty;
            private Func<Item, int, bool> _canAcceptItem;
            private Action<Item, bool> _onItemAddedRemoved;

            private Backpacks _plugin => _backpack.Plugin;
            private Configuration _config => _plugin._config;

            public ItemContainerAdapter()
            {
                _onDirty = () => _backpack.MarkDirty();
                _canAcceptItem = (item, amount) =>
                {
                    // Explicitly track hook time so server owners can be informed of the cost.
                    var result = _backpack.ShouldAcceptItem(item, ItemContainer);
                    if (!result)
                    {
                        var feedbackRecipient = _backpack.DetermineFeedbackRecipientIfEligible();
                        if ((object)feedbackRecipient != null)
                        {
                            feedbackRecipient.ChatMessage(_plugin.GetMessage(feedbackRecipient, "Backpack Item Rejected"));
                            _plugin.SendEffect(feedbackRecipient, _config.ItemRestrictions.FeedbackEffect);
                            _backpack.TimeSinceLastFeedback = 0;
                        }
                    }
                    return result;
                };
                _onItemAddedRemoved = (item, wasAdded) =>
                {
                    _backpack.HandleItemCountChanged();
                };
            }

            public ItemContainerAdapter Setup(Backpack backpack, int pageIndex, ItemContainer container)
            {
                #if DEBUG_POOLING
                LogDebug($"ItemContainerAdapter::Setup | PageIndex: {pageIndex.ToString()} | Capacity: {container.capacity.ToString()}");
                #endif

                PageIndex = pageIndex;
                ItemContainer = container;
                _backpack = backpack;

                return this;
            }

            public void EnterPool()
            {
                #if DEBUG_POOLING
                LogDebug($"ItemContainerAdapter::EnterPool | PageIndex: {PageIndex.ToString()} | Capacity: {Capacity.ToString()} | {PoolUtils.GetStats<ItemContainerAdapter>()}");
                #endif

                PageIndex = 0;
                ItemContainer = null;
                _backpack = null;
            }

            public void LeavePool()
            {
                #if DEBUG_POOLING
                LogDebug($"ItemContainerAdapter::LeavePool | {PoolUtils.GetStats<ItemContainerAdapter>()}");
                #endif
            }

            public ItemContainerAdapter AddDelegates()
            {
                // Add delegates only after filling the container initially to avoid marking the container as dirty
                // before any changes have been made, and avoids unnecessary CanBackpackAcceptItem hook calls.
                ItemContainer.onDirty += _onDirty;
                ItemContainer.canAcceptItem = _canAcceptItem;
                ItemContainer.onItemAddedRemoved += _onItemAddedRemoved;
                return this;
            }

            public void SortByPosition()
            {
                ItemContainer.itemList.Sort((a, b) => a.position.CompareTo(b.position));
            }

            public void FindItems(ref ItemQuery itemQuery, List<Item> collect)
            {
                ItemUtils.FindItems(ItemContainer.itemList, ref itemQuery, collect);
            }

            public void FindAmmo(AmmoTypes ammoType, List<Item> collect)
            {
                ItemContainer.FindAmmo(collect, ammoType);
            }

            public int PositionOf(ref ItemQuery itemQuery)
            {
                SortByPosition();
                return ItemUtils.PositionOf(ItemContainer.itemList, ref itemQuery);
            }

            public int CountItems(ref ItemQuery itemQuery)
            {
                return ItemUtils.CountItems(ItemContainer.itemList, ref itemQuery);
            }

            public int SumItems(ref ItemQuery itemQuery)
            {
                return ItemUtils.SumItems(ItemContainer.itemList, ref itemQuery);
            }

            public int TakeItems(ref ItemQuery itemQuery, int amount, List<Item> collect)
            {
                return ItemUtils.TakeItems(ItemContainer.itemList, ref itemQuery, amount, collect);
            }

            public bool TryDepositItem(Item item)
            {
                return item.MoveToContainer(ItemContainer);
            }

            public bool TryInsertItem(Item item, ref ItemQuery itemQuery, int position)
            {
                for (var i = position; i < ItemContainer.capacity; i++)
                {
                    var existingItem = ItemContainer.GetSlot(i);
                    if (existingItem != null && itemQuery.GetUsableAmount(existingItem) <= 0)
                        continue;

                    if (item.MoveToContainer(ItemContainer, i, allowSwap: false))
                        return true;
                }

                return item.MoveToContainer(ItemContainer);
            }

            public void ReclaimFractionForSoftcore(float fraction, List<Item> collect)
            {
                var itemList = ItemContainer.itemList;

                // For some reason, the vanilla reclaim logic doesn't take the last item.
                if (itemList.Count <= 1)
                    return;

                var numToTake = Mathf.Ceil(itemList.Count * fraction);

                for (var i = 0; i < numToTake; i++)
                {
                    var indexToTake = UnityEngine.Random.Range(0, itemList.Count);
                    var itemToTake = itemList[indexToTake];
                    if (itemToTake.amount > 1)
                    {
                        // Prefer taking a smaller stack if possible (vanilla behavior).
                        foreach (var item in itemList)
                        {
                            if (item.info != itemToTake.info)
                                continue;

                            if (item.amount >= itemToTake.amount)
                                continue;

                            itemToTake = item;
                        }
                    }

                    collect.Add(itemToTake);
                    itemToTake.RemoveFromContainer();
                }
            }

            public void TakeRestrictedItems(List<Item> collect)
            {
                for (var i = ItemContainer.itemList.Count - 1; i >= 0; i--)
                {
                    var item = ItemContainer.itemList[i];
                    if (_backpack.RestrictionRuleset.AllowsItem(item))
                        continue;

                    collect.Add(item);
                    item.RemoveFromContainer();
                }
            }

            public void TakeAllItems(List<Item> collect, int startPosition = 0)
            {
                SortByPosition();

                for (var i = 0; i < ItemContainer.itemList.Count; i++)
                {
                    var item = ItemContainer.itemList[i];
                    if (item.position < startPosition)
                        continue;

                    collect.Add(item);
                    item.RemoveFromContainer();
                    i--;
                }
            }

            public void SerializeForNetwork(List<ProtoBuf.Item> saveList)
            {
                ItemUtils.SerializeForNetwork(ItemContainer.itemList, saveList);
            }

            public void SerializeTo(List<ItemData> saveList, List<ItemData> itemsToReleaseToPool)
            {
                var positionOffset = PageIndex * _maxCapacityPerPage;

                foreach (var item in ItemContainer.itemList)
                {
                    var itemData = Pool.Get<ItemData>().Setup(item, positionOffset);
                    saveList.Add(itemData);
                    itemsToReleaseToPool.Add(itemData);
                }
            }

            public void EraseContents(WipeRuleset ruleset, ref WipeContext wipeContext)
            {
                for (var i = ItemContainer.itemList.Count - 1; i >= 0; i--)
                {
                    var item = ItemContainer.itemList[i];
                    item.RemoveFromContainer();
                    item.Remove();
                }
            }

            public void Kill()
            {
                if (ItemContainer == null || ItemContainer.uid == 0)
                    return;

                ItemContainer.Kill();
            }

            public ItemContainerAdapter CopyItemsFrom(List<ItemData> itemDataList)
            {
                foreach (var itemData in itemDataList)
                {
                    var item = itemData.ToItem();
                    if (item == null)
                        continue;

                    if (!item.MoveToContainer(ItemContainer, item.position) && !item.MoveToContainer(ItemContainer))
                    {
                        _backpack.AddRejectedItem(item);
                    }
                }

                return this;
            }
        }

        /// <summary>
        /// A collection of IContainerAdapters which may contain null entries.
        ///
        /// The underlying array may be enlarged but not shrunk via the Resize method.
        ///
        /// When enumerating via foreach, null entries are skipped, and enumeration stops at Count.
        /// </summary>
        private class ContainerAdapterCollection : IEnumerable<IContainerAdapter>
        {
            private class ContainerAdapterEnumerator : IEnumerator<IContainerAdapter>
            {
                public bool InUse => _position >= 0;
                private int _position = -1;
                private ContainerAdapterCollection _adapterCollection;

                public ContainerAdapterEnumerator(ContainerAdapterCollection adapterCollection)
                {
                    _adapterCollection = adapterCollection;
                }

                public bool MoveNext()
                {
                    while (++_position < _adapterCollection.Count)
                    {
                        if (_adapterCollection[_position] != null)
                            return true;
                    }

                    return false;
                }

                public void Reset()
                {
                    throw new NotImplementedException();
                }

                public IContainerAdapter Current => _adapterCollection[_position];

                object IEnumerator.Current => Current;

                public void Dispose()
                {
                    _position = -1;
                }
            }

            public int Count { get; private set; }
            private IContainerAdapter[] _containerAdapters;
            private ContainerAdapterEnumerator _enumerator;

            public ContainerAdapterCollection(int size)
            {
                Resize(size);
                _enumerator = new ContainerAdapterEnumerator(this);
            }

            public void RemoveAt(int index)
            {
                this[index] = null;
            }

            public IContainerAdapter this[int i]
            {
                get
                {
                    if (i >= Count)
                        throw new IndexOutOfRangeException($"Index {i} was outside the bounds of the collection of size {Count}");

                    return _containerAdapters[i];
                }
                set
                {
                    if (i >= Count)
                        throw new IndexOutOfRangeException($"Index {i} was outside the bounds of the collection of size {Count}");

                    _containerAdapters[i] = value;
                }
            }

            public IEnumerator<IContainerAdapter> GetEnumerator()
            {
                if (_enumerator.InUse)
                    throw new InvalidOperationException($"{nameof(ContainerAdapterEnumerator)} was not disposed after previous use");

                return _enumerator;
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }

            public void Resize(int newSize)
            {
                if (newSize == Count)
                    return;

                if (newSize > Count)
                {
                    Array.Resize(ref _containerAdapters, newSize);
                }
                else
                {
                    for (var i = Count; i < _containerAdapters.Length; i++)
                    {
                        if (_containerAdapters[i] != null)
                            throw new InvalidOperationException($"ContainerAdapterCollection cannot be shrunk from {Count} to {newSize} because there is an existing container adapter at index {i}");
                    }
                }

                Count = newSize;
            }

            public void ResetPooledItemsAndClear()
            {
                PoolUtils.ResetItemsAndClear(_containerAdapters);
                Count = 0;
            }
        }

        #endregion

        #region Player Inventory Watcher

        private class InventoryWatcher : FacepunchBehaviour
        {
            public static InventoryWatcher AddToPlayer(BasePlayer player, Backpack backpack)
            {
                var component = player.gameObject.AddComponent<InventoryWatcher>();
                component._player = player;
                component._backpack = backpack;

                if (player.inventory.containerMain != null)
                    player.inventory.containerMain.onItemAddedRemoved += component._onItemAddedRemoved;

                if (player.inventory.containerBelt != null)
                    player.inventory.containerBelt.onItemAddedRemoved += component._onItemAddedRemoved;

                if (player.inventory.containerWear != null)
                    player.inventory.containerWear.onItemAddedRemoved += component._onItemAddedRemoved;

                return component;
            }

            private BasePlayer _player;
            private Backpack _backpack;

            private Action<Item, bool> _onItemAddedRemoved;
            private int _pauseGatherModeUntilFrame;

            public void DestroyImmediate() => DestroyImmediate(this);

            private InventoryWatcher()
            {
                _onItemAddedRemoved = OnItemAddedRemoved;
            }

            private bool IsLootingBackpackOrChildContainer()
            {
                var lootingContainer = _player.inventory.loot.containers.FirstOrDefault();
                if (lootingContainer == null)
                    return false;

                var rootContainer = lootingContainer.parent != null
                    ? GetRootContainer(lootingContainer.parent)
                    : lootingContainer;

                if (rootContainer == null)
                    return false;

                return _backpack.Plugin._backpackManager.IsBackpack(rootContainer);
            }

            private void OnItemAddedRemoved(Item item, bool wasAdded)
            {
                if (_player.IsDestroyed
                    || _player.IsDead()
                    || _player.IsIncapacitated()
                    || _player.IsSleeping()
                    || _player.IsReceivingSnapshot
                    || IsLootingBackpackOrChildContainer())
                    return;

                if (wasAdded)
                {
                    // Don't gather items from the wearable container.
                    // We still listen to events from it in order to determine when an item is removed.
                    if (item.parent == _player.inventory.containerWear)
                        return;

                    if (_pauseGatherModeUntilFrame != 0)
                    {
                        if (_pauseGatherModeUntilFrame > Time.frameCount)
                            return;

                        _pauseGatherModeUntilFrame = 0;
                    }

                    var itemQuery = ItemQuery.FromItem(item);
                    if (HasMatchingItem(_player.inventory.containerMain.itemList, item, ref itemQuery, 24)
                        || HasMatchingItem(_player.inventory.containerBelt.itemList, item, ref itemQuery, 6))
                        return;

                    var originalPauseGatherModeUntilFrame = _pauseGatherModeUntilFrame;
                    if (_backpack.TryGatherItem(item) && originalPauseGatherModeUntilFrame != _pauseGatherModeUntilFrame)
                    {
                        // Don't pause gather mode due to gathering an item.
                        _pauseGatherModeUntilFrame = 0;
                    }
                }
                else
                {
                    _pauseGatherModeUntilFrame = Time.frameCount + 1;
                }
            }

            private bool HasMatchingItem(List<Item> itemList, Item item, ref ItemQuery itemQuery, int maxSlots)
            {
                for (var i = 0; i < itemList.Count; i++)
                {
                    var possibleItem = itemList[i];
                    if (possibleItem == item || possibleItem.position >= maxSlots)
                        continue;

                    if (itemQuery.GetUsableAmount(possibleItem) > 0)
                        return true;
                }

                return false;
            }

            private void OnDestroy()
            {
                if (_player.inventory.containerMain != null)
                    _player.inventory.containerMain.onItemAddedRemoved -= _onItemAddedRemoved;

                if (_player.inventory.containerBelt != null)
                    _player.inventory.containerBelt.onItemAddedRemoved -= _onItemAddedRemoved;

                if (_player.inventory.containerWear != null)
                    _player.inventory.containerWear.onItemAddedRemoved -= _onItemAddedRemoved;

                _backpack.HandleGatheringStopped();
            }
        }

        #endregion

        #region Backpack

        private enum GatherMode
        {
            // Don't rename these since the names are persisted in data files.
            None = 0,
            All,
            Existing
        }

        [JsonObject(MemberSerialization.OptIn)]
        [JsonConverter(typeof(PoolConverter<Backpack>))]
        private class Backpack : Pool.IPooled
        {
            [Flags]
            public enum Flag
            {
                CapacityCached = 1 << 0,
                RestrictionsCached = 1 << 1,
                GatherCached = 1 << 2,
                RetrieveCached = 1 << 3,
                ProcessedRestrictedItems = 1 << 4,
                Dirty = 1 << 5,
            }

            private class PausableCallback : IDisposable
            {
                private Action _action;
                private bool _isPaused;
                private bool _wasCalled;

                public PausableCallback(Action action)
                {
                    _action = action;
                }

                public PausableCallback Pause()
                {
                    _isPaused = true;
                    return this;
                }

                public void Call()
                {
                    if (_isPaused)
                    {
                        _wasCalled = true;
                        return;
                    }

                    _action();
                }

                public void Dispose()
                {
                    if (_isPaused && _wasCalled)
                    {
                        _action();
                    }

                    _isPaused = false;
                    _wasCalled = false;
                }
            }

            private struct BackpackCapacity
            {
                public static int CalculatePageCapacity(int totalCapacity, int pageIndex)
                {
                    if (pageIndex < 0)
                        throw new ArgumentOutOfRangeException($"Page cannot be negative: {pageIndex}.");

                    var numPages = CalculatePageCountForCapacity(totalCapacity);
                    var lastPageIndex = numPages - 1;

                    if (pageIndex > lastPageIndex)
                        throw new ArgumentOutOfRangeException($"Page {pageIndex} cannot exceed {lastPageIndex}");

                    return pageIndex < lastPageIndex
                        ? _maxCapacityPerPage
                        : totalCapacity - _maxCapacityPerPage * lastPageIndex;
                }

                public static bool operator >(BackpackCapacity a, BackpackCapacity b) => a.Capacity > b.Capacity;
                public static bool operator <(BackpackCapacity a, BackpackCapacity b) => a.Capacity < b.Capacity;

                public static bool operator >=(BackpackCapacity a, BackpackCapacity b) => a.Capacity >= b.Capacity;
                public static bool operator <=(BackpackCapacity a, BackpackCapacity b) => a.Capacity <= b.Capacity;

                private static int CalculatePageCountForCapacity(int capacity)
                {
                    return 1 + (capacity - 1) / _maxCapacityPerPage;
                }

                public int Capacity
                {
                    get
                    {
                        return _capacity;
                    }
                    set
                    {
                        _capacity = value;
                        PageCount = CalculatePageCountForCapacity(value);
                    }
                }
                public int PageCount { get; private set; }
                public int LastPage => PageCount - 1;
                public int LastPageCapacity => CapacityForPage(LastPage);
                public int CapacityForPage(int pageIndex) => CalculatePageCapacity(Capacity, pageIndex);
                public int ClampPage(int pageIndex) => Mathf.Clamp(pageIndex, 0, LastPage);

                private int _capacity;
            }

            private const float FeedbackThrottleSeconds = 1f;

            private static int CalculatePageIndexForItemPosition(int position)
            {
                return position / _maxCapacityPerPage;
            }

            [JsonProperty("OwnerID", Order = 0)]
            public ulong OwnerId { get; private set; }

            [JsonProperty("GatherMode", ItemConverterType = typeof(StringEnumConverter))]
            private Dictionary<int, GatherMode> GatherModeByPage = new Dictionary<int, GatherMode>();

            [JsonProperty("Retrieve", DefaultValueHandling = DefaultValueHandling.Ignore)]
            private int RetrieveFromPagesMask;

            [JsonProperty("Items", Order = 2)]
            private List<ItemData> ItemDataCollection = new List<ItemData>();

            public List<Item> _rejectedItems;

            public Backpacks Plugin;
            public BackpackNetworkController NetworkController { get; private set; }
            public string OwnerIdString;
            public RealTimeSince TimeSinceLastFeedback;

            private BackpackCapacity ActualCapacity;
            private BackpackCapacity _allowedCapacity;

            private PausableCallback _itemCountChangedEvent;
            private Flag _flags;
            private RestrictionRuleset _restrictionRuleset;
            private bool _canGather;
            private bool _canRetrieve;
            private DynamicConfigFile _dataFile;
            private StorageContainer _storageContainer;
            private BasePlayer _owner;
            private ContainerAdapterCollection _containerAdapters;
            private readonly List<BasePlayer> _looters = new List<BasePlayer>();
            private readonly List<BasePlayer> _uiViewers = new List<BasePlayer>();
            private InventoryWatcher _inventoryWatcher;
            private float _pauseGatherModeUntilTime;

            public bool HasLooters => _looters.Count > 0;
            public bool IsGathering => (object)_inventoryWatcher != null;
            private Configuration _config => Plugin._config;
            private BackpackManager _backpackManager => Plugin._backpackManager;
            private SubscriberManager _subscriberManager => Plugin._subscriberManager;

            public BasePlayer Owner
            {
                get
                {
                    if (_owner == null || !_owner.IsConnected)
                    {
                        foreach (var looter in _looters)
                        {
                            if (looter.userID == OwnerId)
                            {
                                _owner = looter;
                                break;
                            }
                        }

                        if (_owner == null)
                        {
                            _owner = BasePlayer.FindByID(OwnerId);
                        }
                    }

                    return _owner;
                }
            }

            public int Capacity => AllowedCapacity.Capacity;

            private BackpackCapacity AllowedCapacity
            {
                get
                {
                    if (!HasFlag(Flag.CapacityCached))
                    {
                        _allowedCapacity.Capacity = Math.Max(MinCapacity, Plugin._backpackCapacityManager.GetCapacity(OwnerId, OwnerIdString));
                        SetFlag(Flag.CapacityCached, true);
                    }

                    return _allowedCapacity;
                }
            }

            public RestrictionRuleset RestrictionRuleset
            {
                get
                {
                    if (!HasFlag(Flag.RestrictionsCached))
                    {
                        var restrictionRuleset = _config.ItemRestrictions.GetForPlayer(OwnerIdString);
                        if (restrictionRuleset != _restrictionRuleset)
                        {
                            // Re-evaluate existing items when the backpack is next opened.
                            SetFlag(Flag.ProcessedRestrictedItems, false);
                        }

                        _restrictionRuleset = restrictionRuleset;
                        SetFlag(Flag.RestrictionsCached, true);
                    }

                    return _restrictionRuleset;
                }
            }

            public bool CanGather
            {
                get
                {
                    if (!HasFlag(Flag.GatherCached))
                    {
                        _canGather = Plugin.permission.UserHasPermission(OwnerIdString, GatherPermission);
                        SetFlag(Flag.GatherCached, true);
                    }

                    return _canGather;
                }
            }

            public bool CanRetrieve
            {
                get
                {
                    if (Plugin.ItemRetriever == null)
                        return false;

                    if (!HasFlag(Flag.RetrieveCached))
                    {
                        _canRetrieve = Plugin.permission.UserHasPermission(OwnerIdString, RetrievePermission);
                        SetFlag(Flag.RetrieveCached, true);
                    }

                    return _canRetrieve;
                }
            }

            public int ItemCount
            {
                get
                {
                    var count = 0;

                    foreach (var containerAdapter in _containerAdapters)
                    {
                        count += containerAdapter.ItemCount;
                    }

                    return count;
                }
            }

            public bool IsRetrieving
            {
                get
                {
                    if (!CanRetrieve)
                        return false;

                    var allowedPageCount = AllowedCapacity.PageCount;

                    for (var pageIndex = 0; pageIndex < allowedPageCount; pageIndex++)
                    {
                        if (IsRetrievingFromPage(pageIndex))
                            return true;
                    }

                    return false;
                }
            }

            private bool HasItems => ItemCount > 0;

            public Backpack()
            {
                _itemCountChangedEvent = new PausableCallback(BroadcastItemCountChanged);
            }

            public void Setup(Backpacks plugin, ulong ownerId, DynamicConfigFile dataFile)
            {
                #if DEBUG_POOLING
                LogDebug($"Backpack::Setup | OwnerId: {ownerId.ToString()}");
                #endif

                Plugin = plugin;
                OwnerId = ownerId;
                OwnerIdString = ownerId.ToString();
                _dataFile = dataFile;

                if (NetworkController == null)
                {
                    NetworkController = BackpackNetworkController.Create();
                }

                SetupItemsAndContainers();
            }

            public void EnterPool()
            {
                #if DEBUG_POOLING
                LogDebug($"Backpack::EnterPool | OwnerId: {OwnerIdString} | {PoolUtils.GetStats<Backpack>()}");
                #endif

                OwnerId = 0;
                if (ItemDataCollection != null)
                {
                    PoolUtils.ResetItemsAndClear(ItemDataCollection);
                }

                // Don't remove the NetworkController. Will reuse it for the next Backpack owner.
                NetworkController?.UnsubscribeAll();
                _itemCountChangedEvent.Dispose();
                _flags = 0;
                OwnerIdString = null;
                ActualCapacity = default(BackpackCapacity);
                _allowedCapacity = default(BackpackCapacity);
                _restrictionRuleset = null;
                _canGather = false;
                _canRetrieve = false;
                _dataFile = null;
                _storageContainer = null;
                _owner = null;
                _containerAdapters?.ResetPooledItemsAndClear();
                _looters.Clear();
                _uiViewers.Clear();
                StopGathering();
                if (_rejectedItems?.Count > 0)
                {
                    foreach (var item in _rejectedItems)
                    {
                        LogError($"Found rejected item when backpack entered pool: {item.amount.ToString()} {item.info.shortname} (skin: {item.skin.ToString()})");
                        item.Remove();
                    }
                    _rejectedItems.Clear();
                }

                Plugin = null;
            }

            public void LeavePool()
            {
                #if DEBUG_POOLING
                LogDebug($"LeavePool | {PoolUtils.GetStats<Backpack>()}");
                #endif
            }

            public void SetFlag(Flag flag, bool value)
            {
                if (value)
                {
                    _flags |= flag;
                }
                else
                {
                    _flags &= ~flag;
                }
            }

            public bool HasFlag(Flag flag)
            {
                return _flags.HasFlag(flag);
            }

            public void DiscoverBags(Plugin bagOfHolding)
            {
                foreach (var containerAdapter in _containerAdapters)
                {
                    var itemContainerAdapter = containerAdapter as ItemContainerAdapter;
                    if (itemContainerAdapter == null)
                        continue;

                    bagOfHolding.Call("API_DiscoverBags", itemContainerAdapter.ItemContainer);
                }
            }

            public void MarkDirty()
            {
                SetFlag(Flag.Dirty, true);

                if (Plugin.ItemRetriever != null)
                {
                    Owner?.inventory?.containerMain?.MarkDirty();
                }
            }

            public bool IsRetrievingFromPage(int pageIndex)
            {
                var flag = 1 << pageIndex;
                return (RetrieveFromPagesMask & flag) != 0;
            }

            public void ToggleRetrieve(BasePlayer player, int pageIndex)
            {
                var wasPreviouslyRetrieving = IsRetrieving;
                SetRetrieveFromPage(pageIndex, !IsRetrievingFromPage(pageIndex));
                MaybeCreateContainerUi(player,  AllowedCapacity.PageCount, pageIndex, EnsurePage(pageIndex).Capacity);

                var isNowRetrieving = IsRetrieving;
                if (isNowRetrieving != wasPreviouslyRetrieving)
                {
                    _subscriberManager.BroadcastRetrieveChanged(this, isNowRetrieving);
                }
            }

            public GatherMode GetGatherModeForPage(int pageIndex)
            {
                GatherMode gatherMode;
                return GatherModeByPage.TryGetValue(pageIndex, out gatherMode)
                    ? gatherMode
                    : GatherMode.None;
            }

            public void ToggleGatherMode(BasePlayer player, int pageIndex)
            {
                switch (GetGatherModeForPage(pageIndex))
                {
                    case GatherMode.All:
                        SetGatherModeForPage(pageIndex, GatherMode.Existing);
                        break;

                    case GatherMode.Existing:
                        SetGatherModeForPage(pageIndex, GatherMode.None);
                        break;

                    case GatherMode.None:
                        SetGatherModeForPage(pageIndex, GatherMode.All);
                        break;
                }

                if (GatherModeByPage.Count > 0)
                {
                    StartGathering(player);
                }
                else
                {
                    StopGathering();
                }

                MaybeCreateContainerUi(player,  AllowedCapacity.PageCount, pageIndex, EnsurePage(pageIndex).Capacity);
            }

            public void HandleGatheringStopped()
            {
                _inventoryWatcher = null;
            }

            public void PauseGatherMode(float durationSeconds)
            {
                if (!IsGathering)
                    return;

                _pauseGatherModeUntilTime = Time.time + durationSeconds;
            }

            public bool TryGatherItem(Item item)
            {
                if (!CanGather)
                {
                    GatherModeByPage.Clear();
                    SetFlag(Flag.Dirty, true);
                    StopGathering();
                    return false;
                }

                // When overflowing, don't allow items to be added.
                if (ActualCapacity > AllowedCapacity)
                    return false;

                if (_pauseGatherModeUntilTime != 0)
                {
                    if (_pauseGatherModeUntilTime > Time.time)
                        return false;

                    _pauseGatherModeUntilTime = 0;
                }

                // Optimization: Don't search pages for a matching item it's not allowed.
                if (_config.ItemRestrictions.Enabled && !RestrictionRuleset.AllowsItem(item))
                    return false;

                var itemQuery = ItemQuery.FromItem(item);
                var anyPagesWithGatherAll = false;
                var allowedPageCount = AllowedCapacity.PageCount;

                using (_itemCountChangedEvent.Pause())
                {
                    // Use a for loop so empty pages aren't skipped.
                    for (var i = 0; i < allowedPageCount; i++)
                    {
                        var gatherMode = GetGatherModeForPage(i);
                        if (gatherMode == GatherMode.None)
                            continue;

                        if (gatherMode == GatherMode.All)
                        {
                            anyPagesWithGatherAll = true;
                            continue;
                        }

                        var containerAdapter = _containerAdapters[i];
                        if (containerAdapter == null || !containerAdapter.HasItems)
                            continue;

                        var position = containerAdapter.PositionOf(ref itemQuery);
                        if (position == -1)
                            continue;

                        if (EnsureItemContainerAdapter(i).TryInsertItem(item, ref itemQuery, position))
                            return true;
                    }

                    if (anyPagesWithGatherAll)
                    {
                        // Try to add the item to a Gather:All page that has a matching stack.
                        // Use a foreach loop to skip uninitialized pages (which are empty).
                        foreach (var containerAdapter in _containerAdapters)
                        {
                            var gatherMode = GetGatherModeForPage(containerAdapter.PageIndex);
                            if (gatherMode != GatherMode.All || !containerAdapter.HasItems)
                                continue;

                            var position = containerAdapter.PositionOf(ref itemQuery);
                            if (position == -1)
                                continue;

                            if (EnsureItemContainerAdapter(containerAdapter.PageIndex)
                                .TryInsertItem(item, ref itemQuery, position))
                                return true;
                        }

                        // Try to add the item to any Gather:All page.
                        // Use a for loop so uninitialized pages aren't skipped.
                        for (var i = 0; i < allowedPageCount; i++)
                        {
                            var gatherMode = GetGatherModeForPage(i);
                            if (gatherMode != GatherMode.All)
                                continue;

                            if (EnsureItemContainerAdapter(i).TryDepositItem(item))
                                return true;
                        }
                    }
                }

                return false;
            }

            public void AddRejectedItem(Item item)
            {
                if (_rejectedItems == null)
                {
                    _rejectedItems = new List<Item>();
                }

                _rejectedItems.Add(item);
            }

            public int GetPageIndexForContainer(ItemContainer container)
            {
                return GetAdapterForContainer(container)?.PageIndex ?? -1;
            }

            public ItemContainerAdapter EnsureItemContainerAdapter(int pageIndex)
            {
                var containerAdapter = EnsurePage(pageIndex, preferRealContainer: true);
                return containerAdapter as ItemContainerAdapter
                       ?? UpgradeToItemContainer(containerAdapter as VirtualContainerAdapter);
            }

            public int GetAllowedPageCapacityForLooter(ulong looterId, int desiredPageIndex)
            {
                return GetAllowedCapacityForLooter(looterId).CapacityForPage(desiredPageIndex);
            }

            public int DetermineInitialPageForLooter(ulong looterId, int desiredPageIndex, bool forward)
            {
                var allowedCapacity = GetAllowedCapacityForLooter(looterId);

                if (desiredPageIndex == -1)
                {
                    desiredPageIndex = forward ? 0 : allowedCapacity.LastPage;
                }

                return allowedCapacity.ClampPage(desiredPageIndex);
            }

            public int DetermineNextPageIndexForLooter(ulong looterId, int currentPageIndex, int desiredPageIndex, bool forward, bool wrapAround, bool requireContents)
            {
                var allowedCapacity = GetAllowedCapacityForLooter(looterId);

                if (desiredPageIndex >= 0)
                    return Math.Min(desiredPageIndex, allowedCapacity.LastPage);

                if (forward)
                {
                    for (var i = currentPageIndex + 1; i < allowedCapacity.PageCount; i++)
                    {
                        var containerAdapter = _containerAdapters[i];
                        if (!requireContents || (containerAdapter?.HasItems ?? false))
                            return i;
                    }

                    if (wrapAround)
                    {
                        for (var i = 0; i < currentPageIndex; i++)
                        {
                            var containerAdapter = _containerAdapters[i];
                            if (!requireContents || (containerAdapter?.HasItems ?? false))
                                return i;
                        }
                    }
                }
                else
                {
                    // Searching backward.
                    for (var i = currentPageIndex - 1; i >= 0; i--)
                    {
                        var containerAdapter = _containerAdapters[i];
                        if (!requireContents || (containerAdapter?.HasItems ?? false))
                            return i;
                    }

                    if (wrapAround)
                    {
                        for (var i = allowedCapacity.LastPage; i > currentPageIndex; i++)
                        {
                            var containerAdapter = _containerAdapters[i];
                            if (!requireContents || (containerAdapter?.HasItems ?? false))
                                return i;
                        }
                    }
                }

                return currentPageIndex;
            }

            public int CountItems(ref ItemQuery itemQuery)
            {
                var count = 0;

                foreach (var containerAdapter in _containerAdapters)
                {
                    count += containerAdapter.CountItems(ref itemQuery);
                }

                return count;
            }

            public void FindItems(ref ItemQuery itemQuery, List<Item> collect, bool forItemRetriever = false)
            {
                foreach (var containerAdapter in _containerAdapters)
                {
                    if (forItemRetriever && !IsRetrievingFromPage(containerAdapter.PageIndex))
                        continue;

                    (containerAdapter as ItemContainerAdapter)?.FindItems(ref itemQuery, collect);
                }
            }

            public void FindAmmo(AmmoTypes ammoType, List<Item> collect, bool forItemRetriever = false)
            {
                foreach (var containerAdapter in _containerAdapters)
                {
                    if (forItemRetriever && !IsRetrievingFromPage(containerAdapter.PageIndex))
                        continue;

                    (containerAdapter as ItemContainerAdapter)?.FindAmmo(ammoType, collect);
                }
            }

            public int SumItems(ref ItemQuery itemQuery, bool forItemRetriever = false)
            {
                var sum = 0;

                foreach (var containerAdapter in _containerAdapters)
                {
                    if (forItemRetriever && !IsRetrievingFromPage(containerAdapter.PageIndex))
                        continue;

                    sum += containerAdapter.SumItems(ref itemQuery);
                }

                return sum;
            }

            public int TakeItems(ref ItemQuery itemQuery, int amount, List<Item> collect, bool forItemRetriever = false)
            {
                using (_itemCountChangedEvent.Pause())
                {
                    var amountTaken = 0;

                    foreach (var containerAdapter in _containerAdapters)
                    {
                        if (forItemRetriever && !IsRetrievingFromPage(containerAdapter.PageIndex))
                            continue;

                        var amountToTake = amount - amountTaken;
                        if (amountToTake <= 0)
                            break;

                        amountTaken += containerAdapter.TakeItems(ref itemQuery, amountToTake, collect);
                    }

                    return amountTaken;
                }
            }

            public bool TryDepositItem(Item item)
            {
                // When overflowing, don't allow items to be added.
                if (ActualCapacity > AllowedCapacity)
                    return false;

                using (_itemCountChangedEvent.Pause())
                {
                    for (var i = 0; i < AllowedCapacity.PageCount; i++)
                    {
                        var containerAdapter = EnsurePage(i);
                        if (!containerAdapter.TryDepositItem(item))
                            continue;

                        return true;
                    }
                }

                return false;
            }

            public void SerializeForNetwork(List<ProtoBuf.Item> saveList, bool forItemRetriever = false)
            {
                foreach (var containerAdapter in _containerAdapters)
                {
                    if (forItemRetriever && !IsRetrievingFromPage(containerAdapter.PageIndex))
                        continue;

                    containerAdapter.SerializeForNetwork(saveList);
                }
            }

            public IPlayer FindOwnerPlayer() => Plugin.covalence.Players.FindPlayerById(OwnerIdString);

            public bool ShouldAcceptItem(Item item, ItemContainer container)
            {
                if (_config.ItemRestrictions.Enabled && !RestrictionRuleset.AllowsItem(item))
                    return false;

                var hookResult = ExposedHooks.CanBackpackAcceptItem(OwnerId, container, item);
                if (hookResult is bool && (bool)hookResult == false)
                    return false;

                return true;
            }

            public void HandleItemCountChanged()
            {
                _itemCountChangedEvent.Call();
            }

            public ItemContainer GetContainer(bool ensureContainer = false)
            {
                if (ensureContainer)
                    return EnsureItemContainerAdapter(0).ItemContainer;

                return (EnsurePage(0) as ItemContainerAdapter)?.ItemContainer;
            }

            public bool TryOpen(BasePlayer looter, int pageIndex = -1)
            {
                if (!Plugin.VerifyCanOpenBackpack(looter, OwnerId))
                    return false;

                EnlargeIfNeeded();

                var allowedCapacity = GetAllowedCapacityForLooter(looter.userID);
                pageIndex = allowedCapacity.ClampPage(pageIndex);
                var itemContainerAdapter = EnsureItemContainerAdapter(pageIndex);

                NetworkController.Subscribe(looter);

                // Some operations are only appropriate for the owner (not for admins viewing the backpack).
                if (looter.userID == OwnerId)
                {
                    EjectRejectedItemsIfNeeded(looter);
                    EjectRestrictedItemsIfNeeded(looter);
                    ShrinkIfNeededAndEjectOverflowingItems(looter);
                    if (CanGather && GatherModeByPage.Count > 0)
                    {
                        StartGathering(looter);
                    }
                }

                if (!_looters.Contains(looter))
                {
                    _looters.Add(looter);
                }

                StartLooting(looter, itemContainerAdapter.ItemContainer, _storageContainer);
                ExposedHooks.OnBackpackOpened(looter, OwnerId, itemContainerAdapter.ItemContainer);
                MaybeCreateContainerUi(looter,  allowedCapacity.PageCount, pageIndex, itemContainerAdapter.Capacity);

                return true;
            }

            public void SwitchToPage(BasePlayer looter, int pageIndex)
            {
                // In case the backpack size permissions changed while open (e.g., a backpack upgrade button).
                EnlargeIfNeeded();

                var itemContainerAdapter = EnsureItemContainerAdapter(pageIndex);
                var itemContainer = itemContainerAdapter.ItemContainer;
                var playerLoot = looter.inventory.loot;
                foreach (var container in playerLoot.containers)
                {
                    container.onDirty -= playerLoot.MarkDirty;
                }

                if (looter.userID == OwnerId)
                {
                    EjectRejectedItemsIfNeeded(looter);

                    // In case the backpack size permissions changed while open.
                    ShrinkIfNeededAndEjectOverflowingItems(looter);
                }

                playerLoot.containers.Clear();
                Interface.CallHook("OnLootEntityEnd", looter, itemContainer.entityOwner);
                Interface.CallHook("OnLootEntity", looter, itemContainer.entityOwner);
                playerLoot.AddContainer(itemContainer);
                playerLoot.SendImmediate();
                ExposedHooks.OnBackpackOpened(looter, OwnerId, itemContainer);
                MaybeCreateContainerUi(looter, GetAllowedCapacityForLooter(looter.userID).PageCount, pageIndex, itemContainerAdapter.Capacity);
            }

            public BasePlayer DetermineFeedbackRecipientIfEligible()
            {
                if (_looters.Count == 0)
                    return null;

                if (TimeSinceLastFeedback < FeedbackThrottleSeconds)
                    return null;

                // Can't know who tried to place the item if there are multiple looters.
                if (_looters.Count > 1)
                    return null;

                return _looters.FirstOrDefault();
            }

            public void OnClosed(BasePlayer looter)
            {
                _looters.Remove(looter);

                if (_uiViewers.Contains(looter))
                {
                    ContainerUi.DestroyUi(looter);
                    _uiViewers.Remove(looter);
                }

                // Clean up the subscription immediately if admin stopped looting.
                // This avoids having to clean up the admin subscriptions some other way which would add complexity.
                if (looter.userID != OwnerId)
                {
                    NetworkController?.Unsubscribe(looter);
                }
            }

            public DroppedItemContainer Drop(Vector3 position, List<DroppedItemContainer> collect = null)
            {
                if (!HasItems)
                    return null;

                var hookResult = ExposedHooks.CanDropBackpack(OwnerId, position);
                if (hookResult is bool && (bool)hookResult == false)
                    return null;

                ForceCloseAllLooters();
                ReclaimItemsForSoftcore();

                // Check again since the items may have all been reclaimed for Softcore.
                if (!HasItems)
                    return null;

                DroppedItemContainer firstContainer = null;

                using (_itemCountChangedEvent.Pause())
                {
                    using (var itemList = DisposableList<Item>.Get())
                    {
                        foreach (var containerAdapter in _containerAdapters)
                        {
                            if (!containerAdapter.HasItems)
                                continue;

                            containerAdapter.TakeAllItems(itemList);
                            var droppedItemContainer = SpawnDroppedBackpack(position, containerAdapter.Capacity, itemList);
                            if (droppedItemContainer == null)
                                break;

                            itemList.Clear();

                            if ((object)firstContainer == null)
                            {
                                firstContainer = droppedItemContainer;
                            }

                            collect?.Add(droppedItemContainer);
                        }

                        if (itemList.Count > 0)
                        {
                            foreach (var item in itemList)
                            {
                                item.Drop(position, UnityEngine.Random.insideUnitSphere, Quaternion.identity);
                            }
                        }
                    }
                }

                return firstContainer;
            }

            public void EraseContents(WipeRuleset wipeRuleset = null, bool force = false)
            {
                // Optimization: If no container and no stored data, don't bother with the rest of the logic.
                var originalItemCount = ItemCount;
                if (originalItemCount == 0)
                    return;

                if (!force)
                {
                    var hookResult = ExposedHooks.CanEraseBackpack(OwnerId);
                    if (hookResult is bool && (bool)hookResult == false)
                        return;
                }

                var wipeContext = new WipeContext();

                using (_itemCountChangedEvent.Pause())
                {
                    foreach (var containerAdapter in _containerAdapters)
                    {
                        containerAdapter.EraseContents(wipeRuleset, ref wipeContext);
                    }
                }

                if (ItemCount != originalItemCount)
                {
                    HandleItemCountChanged();
                }
            }

            public bool SaveIfChanged()
            {
                if (!HasFlag(Flag.Dirty))
                    return false;

                #if DEBUG_BACKPACK_LIFECYCLE
                LogDebug($"Backpack::Save | {OwnerIdString} | Frame: {Time.frameCount.ToString()}");
                #endif

                using (var itemsToReleaseToPool = DisposableList<ItemData>.Get())
                {
                    foreach (var containerAdapter in _containerAdapters)
                    {
                        containerAdapter.SerializeTo(ItemDataCollection, itemsToReleaseToPool);
                    }

                    SerializeRejectedItems(itemsToReleaseToPool);

                    _dataFile.WriteObject(this);
                    SetFlag(Flag.Dirty, false);

                    // After saving, unused ItemData instances can be pooled.
                    PoolUtils.ResetItemsAndClear(itemsToReleaseToPool);
                }

                // Clear the list, but don't reset the items to the pool, since they have been referenced in the container adapters.
                ItemDataCollection.Clear();

                return true;
            }

            public int FetchItems(BasePlayer player, ref ItemQuery itemQuery, int desiredAmount)
            {
                using (var collect = DisposableList<Item>.Get())
                {
                    var amountTaken = TakeItems(ref itemQuery, desiredAmount, collect);

                    if (amountTaken > 0)
                    {
                        PauseGatherMode(1f);

                        foreach (var item in collect)
                        {
                            player.GiveItem(item);
                        }

                        _pauseGatherModeUntilTime = 0;
                    }

                    return amountTaken;
                }
            }

            public void Kill()
            {
                #if DEBUG_BACKPACK_LIFECYCLE
                LogDebug($"Backpack::Kill | OwnerId: {OwnerIdString} | Frame: {Time.frameCount.ToString()}");
                #endif

                ForceCloseAllLooters();

                foreach (var containerAdapter in _containerAdapters)
                {
                    var adapter = containerAdapter;
                    KillContainerAdapter(ref adapter);
                }

                if (_rejectedItems?.Count > 0)
                {
                    foreach (var item in _rejectedItems)
                    {
                        item.Remove();
                    }

                    _rejectedItems.Clear();
                }

                if (_storageContainer != null && !_storageContainer.IsDestroyed)
                {
                    // Note: The ItemContainer will already be Kill()'d by this point, but that's OK.
                    _storageContainer.Kill();
                }
            }

            public string SerializeContentsAsJson()
            {
                using (var itemsToReleaseToPool = DisposableList<ItemData>.Get())
                {
                    foreach (var containerAdapter in _containerAdapters)
                    {
                        containerAdapter.SerializeTo(ItemDataCollection, itemsToReleaseToPool);
                    }

                    SerializeRejectedItems(itemsToReleaseToPool);

                    var json = JsonConvert.SerializeObject(ItemDataCollection);

                    // After saving, unused ItemData instances can be pooled.
                    PoolUtils.ResetItemsAndClear(itemsToReleaseToPool);

                    // Clear the list, but don't reset the items to the pool, since they have been referenced in the container adapters.
                    ItemDataCollection.Clear();

                    return json;
                }
            }

            public void WriteContentsFromJson(string json)
            {
                var itemDataList = JsonConvert.DeserializeObject<List<ItemData>>(json);

                Kill();

                foreach (var itemData in itemDataList)
                {
                    ItemDataCollection.Add(itemData);
                }

                SetupItemsAndContainers();

                SetFlag(Flag.Dirty, true);
                SaveIfChanged();
            }

            private void CreateContainerAdapters()
            {
                var previousPageIndex = -1;

                // This assumes the collection has been sorted by item position.
                foreach (var itemData in ItemDataCollection)
                {
                    var pageIndex = CalculatePageIndexForItemPosition(itemData.Position);
                    if (pageIndex < previousPageIndex)
                        throw new InvalidOperationException("Found an item for an earlier page while setting up a virtual container. This should not happen.");

                    // Skip items for the previously created page, since creating the page would have copied all items.
                    if (pageIndex == previousPageIndex)
                        continue;

                    // Create an adapter for the page, copying all items.
                    _containerAdapters[pageIndex] = CreateVirtualContainerAdapter(pageIndex)
                        .CopyItemsFrom(ItemDataCollection);

                    previousPageIndex = pageIndex;
                }

                // Clear the list, but don't reset the items to the pool, since they have been referenced in the container adapters.
                ItemDataCollection.Clear();
            }

            private void SetupItemsAndContainers()
            {
                // Sort the items so it's easier to partition the list for multiple pages.
                ItemDataCollection.Sort((a, b) => a.Position.CompareTo(b.Position));

                // Allow the backpack to start beyond the allowed capacity.
                // Overflowing items will be handled when the backpack is opened by its owner.
                var highestUsedPosition = ItemDataCollection.LastOrDefault()?.Position ?? 0;
                ActualCapacity.Capacity = Math.Max(_allowedCapacity.Capacity, highestUsedPosition + 1);

                var pageCount = ActualCapacity.PageCount;
                if (_containerAdapters == null)
                {
                    _containerAdapters = new ContainerAdapterCollection(pageCount);
                }
                else
                {
                    _containerAdapters.Resize(pageCount);
                }

                CreateContainerAdapters();
            }

            private VirtualContainerAdapter CreateVirtualContainerAdapter(int pageIndex)
            {
                return Pool.Get<VirtualContainerAdapter>().Setup(this, pageIndex, ActualCapacity.CapacityForPage(pageIndex));
            }

            private ItemContainerAdapter CreateItemContainerAdapter(int pageIndex)
            {
                var container = CreateContainerForPage(pageIndex, ActualCapacity.CapacityForPage(pageIndex));
                return Pool.Get<ItemContainerAdapter>().Setup(this, pageIndex, container);
            }

            private ItemContainerAdapter UpgradeToItemContainer(VirtualContainerAdapter virtualContainerAdapter)
            {
                // Must cache the page index since it will be reset when pooled.
                var pageIndex = virtualContainerAdapter.PageIndex;
                var itemContainerAdapter = CreateItemContainerAdapter(pageIndex)
                    .CopyItemsFrom(virtualContainerAdapter.ItemDataList)
                    .AddDelegates();

                Pool.Free(ref virtualContainerAdapter);

                _containerAdapters[pageIndex] = itemContainerAdapter;
                return itemContainerAdapter;
            }

            private void SerializeRejectedItems(List<ItemData> itemsToReleaseToPool)
            {
                if (_rejectedItems == null || _rejectedItems.Count == 0)
                    return;

                var lastPosition = ItemDataCollection.LastOrDefault()?.Position ?? 0;

                foreach (var item in _rejectedItems)
                {
                    item.position = ++lastPosition;
                    var itemData = Pool.Get<ItemData>().Setup(item);
                    ItemDataCollection.Add(itemData);
                    itemsToReleaseToPool.Add(itemData);
                }
            }

            private void EjectRejectedItemsIfNeeded(BasePlayer receiver)
            {
                if (_rejectedItems == null || _rejectedItems.Count == 0)
                    return;

                foreach (var item in _rejectedItems)
                {
                    receiver.GiveItem(item);
                }

                _rejectedItems.Clear();
                BroadcastItemCountChanged();
                SetFlag(Flag.Dirty, true);

                receiver.ChatMessage(Plugin.GetMessage(receiver, "Backpack Items Rejected"));
            }

            private void EjectRestrictedItemsIfNeeded(BasePlayer receiver)
            {
                if (!Plugin._config.ItemRestrictions.Enabled)
                    return;

                // Optimization: Avoid processing item restrictions every time the backpack is opened.
                if (HasFlag(Flag.ProcessedRestrictedItems))
                    return;

                using (var ejectedItems = DisposableList<Item>.Get())
                {
                    using (_itemCountChangedEvent.Pause())
                    {
                        foreach (var containerAdapter in _containerAdapters)
                        {
                            containerAdapter.TakeRestrictedItems(ejectedItems);
                        }
                    }

                    if (ejectedItems.Count > 0)
                    {
                        foreach (var item in ejectedItems)
                        {
                            receiver.GiveItem(item);
                        }

                        receiver.ChatMessage(Plugin.GetMessage(receiver, "Blacklisted Items Removed"));
                    }
                }

                SetFlag(Flag.ProcessedRestrictedItems, true);
            }

            private void ShrinkIfNeededAndEjectOverflowingItems(BasePlayer overflowRecipient)
            {
                var allowedCapacity = AllowedCapacity;
                if (ActualCapacity <= allowedCapacity)
                    return;

                var allowedLastPageCapacity = allowedCapacity.LastPageCapacity;

                var itemsDroppedOrGivenToPlayer = 0;

                using (var overflowingItems = DisposableList<Item>.Get())
                {
                    var lastAllowedContainerAdapter = _containerAdapters[allowedCapacity.LastPage];
                    if (lastAllowedContainerAdapter != null)
                    {
                        lastAllowedContainerAdapter.TakeAllItems(overflowingItems, allowedLastPageCapacity);
                        lastAllowedContainerAdapter.Capacity = allowedLastPageCapacity;

                        if (allowedLastPageCapacity > 0)
                        {
                            // Try to give the items to the original page first.
                            var lastAllowedItemContainerAdapter = EnsureItemContainerAdapter(allowedCapacity.LastPage);

                            for (var i = 0; i < overflowingItems.Count; i++)
                            {
                                if (overflowingItems[i].MoveToContainer(lastAllowedItemContainerAdapter.ItemContainer))
                                {
                                    overflowingItems.RemoveAt(i--);
                                }
                            }
                        }
                    }

                    for (var i = allowedCapacity.PageCount; i < ActualCapacity.PageCount; i++)
                    {
                        var containerAdapter = _containerAdapters[i];
                        if (containerAdapter == null)
                            continue;

                        containerAdapter.TakeAllItems(overflowingItems);
                        KillContainerAdapter(ref containerAdapter);
                    }

                    foreach (var item in overflowingItems)
                    {
                        var wasItemAddedToBackpack = false;

                        for (var i = 0; i < allowedCapacity.PageCount; i++)
                        {
                            // Simplification: Make all potential destination containers real containers.
                            var itemContainerAdapter = EnsureItemContainerAdapter(i);
                            if (itemContainerAdapter.TryDepositItem(item))
                            {
                                wasItemAddedToBackpack = true;
                                break;
                            }
                        }

                        if (!wasItemAddedToBackpack)
                        {
                            overflowRecipient.GiveItem(item);
                            itemsDroppedOrGivenToPlayer++;
                        }
                    }
                }

                if (itemsDroppedOrGivenToPlayer > 0)
                {
                    overflowRecipient.ChatMessage(Plugin.GetMessage(overflowRecipient, "Backpack Over Capacity"));
                }

                ActualCapacity = AllowedCapacity;
            }

            private void SetupContainer(ItemContainer container)
            {
                _backpackManager.RegisterContainer(container, this);
            }

            private ItemContainer CreateContainerForPage(int page, int capacity)
            {
                if ((object)_storageContainer == null || _storageContainer.IsDestroyed)
                {
                    _storageContainer = SpawnStorageContainer(0);
                    if ((object)_storageContainer == null)
                        return null;
                }

                if (page == 0)
                {
                    _storageContainer.inventory.capacity = capacity;
                    SetupContainer(_storageContainer.inventory);
                    return _storageContainer.inventory;
                }

                var itemContainer = CreateItemContainer(capacity, _storageContainer);
                SetupContainer(itemContainer);
                return itemContainer;
            }

            private ItemContainerAdapter GetAdapterForContainer(ItemContainer container)
            {
                foreach (var containerAdapter in _containerAdapters)
                {
                    var itemContainerAdapter = containerAdapter as ItemContainerAdapter;
                    if (itemContainerAdapter?.ItemContainer != container)
                        continue;

                    return itemContainerAdapter;
                }

                return null;
            }

            private IContainerAdapter EnsurePage(int pageIndex, bool preferRealContainer = false)
            {
                var containerAdapter = _containerAdapters[pageIndex];
                if (containerAdapter == null)
                {
                    if (preferRealContainer)
                    {
                        containerAdapter = CreateItemContainerAdapter(pageIndex).AddDelegates();
                    }
                    else
                    {
                        containerAdapter = CreateVirtualContainerAdapter(pageIndex);
                    }

                    _containerAdapters[pageIndex] = containerAdapter;
                }

                return containerAdapter;
            }

            private BackpackCapacity GetAllowedCapacityForLooter(ulong looterId)
            {
                return looterId == OwnerId ? AllowedCapacity : ActualCapacity;
            }

            private DroppedItemContainer SpawnDroppedBackpack(Vector3 position, int capacity, List<Item> itemList)
            {
                var entity = GameManager.server.CreateEntity(DroppedBackpackPrefab, position);
                if (entity == null)
                {
                    LogError($"Failed to create entity: {DroppedBackpackPrefab}");
                    return null;
                }

                var droppedItemContainer = entity as DroppedItemContainer;
                if (droppedItemContainer == null)
                {
                    LogError($"Entity is not an instance of DroppedItemContainer: {DroppedBackpackPrefab}");
                    return null;
                }

                droppedItemContainer.gameObject.AddComponent<NoRagdollCollision>();

                droppedItemContainer.lootPanelName = ResizableLootPanelName;
                droppedItemContainer.playerName = $"{FindOwnerPlayer()?.Name ?? "Somebody"}'s Backpack";
                droppedItemContainer.playerSteamID = OwnerId;

                droppedItemContainer.inventory = new ItemContainer();
                droppedItemContainer.inventory.ServerInitialize(null, capacity);
                droppedItemContainer.inventory.GiveUID();
                droppedItemContainer.inventory.entityOwner = droppedItemContainer;
                droppedItemContainer.inventory.SetFlag(ItemContainer.Flag.NoItemInput, true);

                foreach (var item in itemList)
                {
                    if (!item.MoveToContainer(droppedItemContainer.inventory))
                    {
                        item.Remove();
                    }
                }

                droppedItemContainer.Spawn();
                droppedItemContainer.ResetRemovalTime(Math.Max(Plugin._config.MinimumDespawnTime, droppedItemContainer.CalculateRemovalTime()));

                return droppedItemContainer;
            }

            private void EnlargeIfNeeded()
            {
                var allowedCapacity = AllowedCapacity;
                if (ActualCapacity >= allowedCapacity)
                    return;

                var allowedPageCount = allowedCapacity.PageCount;
                if (_containerAdapters.Count < allowedPageCount)
                {
                    _containerAdapters.Resize(allowedPageCount);
                }

                for (var i = 0; i < allowedPageCount; i++)
                {
                    var containerAdapter = _containerAdapters[i];
                    if (containerAdapter == null)
                        continue;

                    var allowedPageCapacity = allowedCapacity.CapacityForPage(i);
                    if (containerAdapter.Capacity < allowedPageCapacity)
                    {
                        containerAdapter.Capacity = allowedPageCapacity;
                    }
                }

                ActualCapacity = AllowedCapacity;
            }

            private void KillContainerAdapter<T>(ref T containerAdapter) where T : class, IContainerAdapter
            {
                #if DEBUG_BACKPACK_LIFECYCLE
                LogDebug($"Backpack::KillContainerAdapter({typeof(T).Name}) | OwnerId: {OwnerIdString} | PageIndex: {containerAdapter.PageIndex.ToString()} | Capacity: {containerAdapter.Capacity.ToString()} ");
                #endif

                var itemContainerAdapter = containerAdapter as ItemContainerAdapter;
                if (itemContainerAdapter != null)
                {
                    _backpackManager.UnregisterContainer(itemContainerAdapter.ItemContainer);
                }

                containerAdapter.Kill();
                _containerAdapters.RemoveAt(containerAdapter.PageIndex);
                Pool.Free(ref containerAdapter);
            }

            private void ForceCloseLooter(BasePlayer looter)
            {
                looter.inventory.loot.Clear();
                looter.inventory.loot.MarkDirty();
                looter.inventory.loot.SendImmediate();

                OnClosed(looter);
            }

            private void ForceCloseAllLooters()
            {
                for (var i = _looters.Count - 1; i >= 0; i--)
                {
                    ForceCloseLooter(_looters[i]);
                }
            }

            private StorageContainer SpawnStorageContainer(int capacity)
            {
                var storageEntity = GameManager.server.CreateEntity(CoffinPrefab, new Vector3(0, -500, 0));
                if (storageEntity == null)
                    return null;

                var containerEntity = storageEntity as StorageContainer;
                if (containerEntity == null)
                {
                    UnityEngine.Object.Destroy(storageEntity.gameObject);
                    return null;
                }

                containerEntity.SetFlag(BaseEntity.Flags.Disabled, true);

                UnityEngine.Object.DestroyImmediate(containerEntity.GetComponent<DestroyOnGroundMissing>());
                UnityEngine.Object.DestroyImmediate(containerEntity.GetComponent<GroundWatch>());

                foreach (var collider in containerEntity.GetComponentsInChildren<Collider>())
                    UnityEngine.Object.DestroyImmediate(collider);

                containerEntity.CancelInvoke(containerEntity.DecayTick);

                BackpackCloseListener.AddToBackpackStorage(Plugin, containerEntity, this);

                containerEntity.baseProtection = Plugin._immortalProtection;
                containerEntity.panelName = ResizableLootPanelName;

                // Temporarily disable networking to prevent initially sending the entity to clients based on the positional network group.
                containerEntity._limitedNetworking = true;

                containerEntity.EnableSaving(false);
                containerEntity.Spawn();

                // Must change the network group after spawning,
                // or else vanilla UpdateNetworkGroup will switch it to a positional network group.
                containerEntity.net.SwitchGroup(NetworkController.NetworkGroup);

                // Re-enable networking now that the entity is in the correct network group.
                containerEntity._limitedNetworking = false;

                containerEntity.inventory.allowedContents = ItemContainer.ContentsType.Generic;
                containerEntity.inventory.capacity = capacity;

                return containerEntity;
            }

            private void ReclaimItemsForSoftcore()
            {
                var softcoreGameMode = BaseGameMode.svActiveGameMode as GameModeSoftcore;
                if ((object)softcoreGameMode == null || (object)ReclaimManager.instance == null)
                    return;

                var reclaimFraction = Plugin._config.Softcore.ReclaimFraction;
                if (reclaimFraction <= 0)
                    return;

                using (var allItemsToReclaim = DisposableList<Item>.Get())
                {
                    using (_itemCountChangedEvent.Pause())
                    {
                        foreach (var containerAdapter in _containerAdapters)
                        {
                            containerAdapter.ReclaimFractionForSoftcore(reclaimFraction, allItemsToReclaim);
                        }
                    }

                    if (allItemsToReclaim.Count > 0)
                    {
                        // There's a vanilla issue where accessing the reclaim backpack will erase items in the reclaim entry above 32.
                        // So we just add new reclaim entries which can only be accessed at the terminal to avoid this issue.
                        // Additionally, reclaim entries have a max size, so we may need to create multiple.
                        while (allItemsToReclaim.Count > ReclaimEntryMaxSize)
                        {
                            using (var itemsToReclaimForEntry = DisposableList<Item>.Get())
                            {
                                for (var i = 0; i < ReclaimEntryMaxSize; i++)
                                {
                                    itemsToReclaimForEntry.Add(allItemsToReclaim[i]);
                                    allItemsToReclaim.RemoveAt(i);
                                }
                                ReclaimManager.instance.AddPlayerReclaim(OwnerId, itemsToReclaimForEntry);
                            }
                        }

                        ReclaimManager.instance.AddPlayerReclaim(OwnerId, allItemsToReclaim);
                    }
                }
            }

            private void SetRetrieveFromPage(int pageIndex, bool retrieve)
            {
                if (pageIndex > 31)
                    return;

                var flag = 1 << pageIndex;

                if (retrieve)
                {
                    RetrieveFromPagesMask |= flag;
                }
                else
                {
                    RetrieveFromPagesMask &= ~flag;
                }

                MarkDirty();
            }

            private void SetGatherModeForPage(int pageIndex, GatherMode gatherMode)
            {
                if (gatherMode == GatherMode.None)
                {
                    GatherModeByPage.Remove(pageIndex);
                }
                else
                {
                    GatherModeByPage[pageIndex] = gatherMode;
                }

                SetFlag(Flag.Dirty, true);
            }

            private void StartGathering(BasePlayer player)
            {
                if (IsGathering)
                    return;

                _inventoryWatcher = InventoryWatcher.AddToPlayer(player, this);
                _subscriberManager.BroadcastGatherChanged(this, true);
            }

            private void StopGathering()
            {
                if (!IsGathering)
                    return;

                _inventoryWatcher.DestroyImmediate();
                _pauseGatherModeUntilTime = 0;
                _subscriberManager.BroadcastGatherChanged(this, false);
            }

            private void BroadcastItemCountChanged()
            {
                _subscriberManager.BroadcastItemCountChanged(this);
            }

            private void MaybeCreateContainerUi(BasePlayer looter, int allowedPageCount, int pageIndex, int containerCapacity)
            {
                if (!CanGather && !CanRetrieve && allowedPageCount <= 1)
                    return;

                ContainerUi.CreateContainerUi(looter, allowedPageCount, pageIndex, containerCapacity, this);

                if (!_uiViewers.Contains(looter))
                {
                    _uiViewers.Add(looter);
                }
            }
        }

        [JsonConverter(typeof(PoolConverter<EntityData>))]
        private class EntityData : Pool.IPooled
        {
            [JsonProperty("Flags", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public BaseEntity.Flags Flags;

            [JsonProperty("DataInt", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public int DataInt;

            [JsonProperty("CreatorSteamId", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public ulong CreatorSteamId;

            [JsonProperty("FileContent", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public string[] FileContent;

            public void Setup(BaseEntity entity)
            {
                var photoEntity = entity as PhotoEntity;
                if ((object)photoEntity != null)
                {
                    if (photoEntity.ImageCrc == 0)
                        return;

                    var fileContent = FileStorage.server.Get(photoEntity.ImageCrc, FileStorage.Type.jpg, entity.net.ID);
                    if (fileContent == null)
                        return;

                    CreatorSteamId = photoEntity.PhotographerSteamId;
                    FileContent = new[] { Convert.ToBase64String(fileContent) };
                    return;
                }

                var signContent = entity as SignContent;
                if ((object)signContent != null)
                {
                    var imageIdList = signContent.GetContentCRCs;

                    var hasContent = false;
                    foreach (var imageId in imageIdList)
                    {
                        if (imageId != 0)
                        {
                            hasContent = true;
                            break;
                        }
                    }

                    if (!hasContent)
                        return;

                    FileContent = new string[imageIdList.Length];

                    for (var i = 0; i < imageIdList.Length; i++)
                    {
                        var imageId = imageIdList[i];
                        if (imageId == 0)
                            continue;

                        var fileContent = FileStorage.server.Get(imageId, FileStorage.Type.png, entity.net.ID);
                        if (fileContent == null)
                            continue;

                        FileContent[i] = Convert.ToBase64String(fileContent);
                    }

                    return;
                }

                var paintedItemStorageEntity = entity as PaintedItemStorageEntity;
                if ((object)paintedItemStorageEntity != null)
                {
                    if (paintedItemStorageEntity._currentImageCrc == 0)
                        return;

                    var fileContent = FileStorage.server.Get(paintedItemStorageEntity._currentImageCrc, FileStorage.Type.png, entity.net.ID);
                    if (fileContent == null)
                        return;

                    FileContent = new[] { Convert.ToBase64String(fileContent) };
                    return;
                }

                var cassette = entity as Cassette;
                if ((object)cassette != null)
                {
                    DataInt = cassette.preloadedAudioId;

                    if (cassette.AudioId == 0)
                        return;

                    var fileContent = FileStorage.server.Get(cassette.AudioId, FileStorage.Type.ogg, entity.net.ID);
                    if (fileContent == null)
                        return;

                    CreatorSteamId = cassette.CreatorSteamId;
                    FileContent = new[] { Convert.ToBase64String(fileContent) };
                    return;
                }

                var pagerEntity = entity as PagerEntity;
                if ((object)pagerEntity != null)
                {
                    Flags = pagerEntity.flags;
                    DataInt = pagerEntity.GetFrequency();
                    return;
                }

                var mobileInventoryEntity = entity as MobileInventoryEntity;
                if ((object)mobileInventoryEntity != null)
                {
                    Flags = mobileInventoryEntity.flags;
                    return;
                }

                LogWarning($"Unable to serialize associated entity of type {entity.GetType()}.");
            }

            public void EnterPool()
            {
                #if DEBUG_POOLING
                LogDebug($"EntityData::EnterPool | {PoolUtils.GetStats<EntityData>()}");
                #endif

                Flags = 0;
                DataInt = 0;
                CreatorSteamId = 0;
                FileContent = null;
            }

            public void LeavePool()
            {
                #if DEBUG_POOLING
                LogDebug($"EntityData::LeavePool | {PoolUtils.GetStats<EntityData>()}");
                #endif
            }

            public void UpdateAssociatedEntity(Item item)
            {
                BaseEntity entity;

                var entityId = item.instanceData?.subEntity ?? 0;
                if (entityId == 0)
                {
                    var itemModSign = item.info.GetComponent<ItemModSign>();
                    if (itemModSign == null)
                        return;

                    entity = itemModSign.CreateAssociatedEntity(item);
                }
                else
                {
                    entity = BaseNetworkable.serverEntities.Find(entityId) as BaseEntity;
                    if (entity == null)
                        return;
                }

                var photoEntity = entity as PhotoEntity;
                if ((object)photoEntity != null)
                {
                    var fileContent = FileContent?.FirstOrDefault();
                    if (fileContent == null)
                        return;

                    photoEntity.SetImageData(CreatorSteamId, Convert.FromBase64String(fileContent));
                    return;
                }

                var signContent = entity as SignContent;
                if ((object)signContent != null)
                {
                    if (FileContent == null)
                        return;

                    for (uint i = 0; i < FileContent.Length && i < signContent.GetContentCRCs.Length; i++)
                    {
                        var fileContent = FileContent[i];
                        if (fileContent == null)
                            continue;

                        signContent.GetContentCRCs[i] = FileStorage.server.Store(Convert.FromBase64String(fileContent), FileStorage.Type.png, entity.net.ID, i);
                    }
                    return;
                }

                var paintedItemStorageEntity = entity as PaintedItemStorageEntity;
                if ((object)paintedItemStorageEntity != null)
                {
                    var fileContent = FileContent?.FirstOrDefault();
                    if (fileContent == null)
                        return;

                    paintedItemStorageEntity._currentImageCrc = FileStorage.server.Store(Convert.FromBase64String(fileContent), FileStorage.Type.png, entity.net.ID);
                    return;
                }

                var cassette = entity as Cassette;
                if ((object)cassette != null)
                {
                    cassette.preloadedAudioId = DataInt;

                    var fileContent = FileContent?.FirstOrDefault();
                    if (fileContent == null)
                        return;

                    var audioId = FileStorage.server.Store(Convert.FromBase64String(fileContent), FileStorage.Type.ogg, entity.net.ID);
                    cassette.SetAudioId(audioId, CreatorSteamId);
                    return;
                }

                var pagerEntity = entity as PagerEntity;
                if ((object)pagerEntity != null)
                {
                    pagerEntity.flags |= Flags;
                    pagerEntity.ChangeFrequency(DataInt);
                    return;
                }

                var mobileInventoryEntity = entity as MobileInventoryEntity;
                if ((object)mobileInventoryEntity != null)
                {
                    mobileInventoryEntity.flags |= Flags;
                    return;
                }
            }
        }

        [JsonConverter(typeof(PoolConverter<ItemData>))]
        private class ItemData : Pool.IPooled
        {
            [JsonProperty("ID")]
            public int ID { get; private set; }

            [JsonProperty("Position")]
            public int Position { get; set; } = -1;

            [JsonProperty("Amount")]
            public int Amount { get; private set; }

            [JsonProperty("IsBlueprint", DefaultValueHandling = DefaultValueHandling.Ignore)]
            private bool IsBlueprint;

            [JsonProperty("BlueprintTarget", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public int BlueprintTarget { get; private set; }

            [JsonProperty("Skin", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public ulong Skin { get; private set; }

            [JsonProperty("Fuel", DefaultValueHandling = DefaultValueHandling.Ignore)]
            private float Fuel;

            [JsonProperty("FlameFuel", DefaultValueHandling = DefaultValueHandling.Ignore)]
            private int FlameFuel;

            [JsonProperty("Condition", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public float Condition { get; private set; }

            [JsonProperty("MaxCondition", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public float MaxCondition { get; private set; } = -1;

            [JsonProperty("Ammo", DefaultValueHandling = DefaultValueHandling.Ignore)]
            private int Ammo;

            [JsonProperty("AmmoType", DefaultValueHandling = DefaultValueHandling.Ignore)]
            private int AmmoType;

            [JsonProperty("DataInt", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public int DataInt { get; private set; }

            [JsonProperty("Name", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public string Name { get; private set; }

            [JsonProperty("Text", DefaultValueHandling = DefaultValueHandling.Ignore)]
            private string Text;

            [JsonProperty("Flags", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public Item.Flag Flags { get; private set; }

            [JsonProperty("EntityData", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public EntityData EntityData { get; private set; }

            [JsonProperty("Contents", DefaultValueHandling = DefaultValueHandling.Ignore)]
            [JsonConverter(typeof(PoolListConverter<ItemData>))]
            public List<ItemData> Contents { get; private set; }

            public ItemData Setup(Item item, int positionOffset = 0)
            {
                #if DEBUG_POOLING
                LogDebug($"ItemData::Setup | {item.amount.ToString()} {item.info.shortname}");
                #endif

                ID = item.info.itemid;
                Position = item.position + positionOffset;
                Amount = item.amount;
                IsBlueprint = item.IsBlueprint();
                BlueprintTarget = item.blueprintTarget;
                Skin = item.skin;
                Fuel = item.fuel;
                FlameFuel = item.GetHeldEntity()?.GetComponent<FlameThrower>()?.ammo ?? 0;
                Condition = item.condition;
                MaxCondition = item.maxCondition;
                Ammo = item.GetHeldEntity()?.GetComponent<BaseProjectile>()?.primaryMagazine?.contents ?? 0;
                AmmoType = item.GetHeldEntity()?.GetComponent<BaseProjectile>()?.primaryMagazine?.ammoType?.itemid ?? 0;
                DataInt = item.instanceData?.dataInt ?? 0;
                Name = item.name;
                Text = item.text;
                Flags = item.flags;

                var subEntityId = item.instanceData?.subEntity ?? 0;
                if (subEntityId != 0)
                {
                    var subEntity = BaseNetworkable.serverEntities.Find(subEntityId) as BaseEntity;
                    if (subEntity != null)
                    {
                        if (EntityData == null)
                        {
                            EntityData = Pool.Get<EntityData>();
                        }
                        EntityData.Setup(subEntity);
                    }
                }

                if (item.contents != null)
                {
                    Contents = Pool.GetList<ItemData>();
                    foreach (var childItem in item.contents.itemList)
                    {
                        Contents.Add(Pool.Get<ItemData>().Setup(childItem));
                    }
                }

                return this;
            }

            public void EnterPool()
            {
                #if DEBUG_POOLING
                LogDebug($"ItemData::EnterPool | {Amount.ToString()} {ItemManager.FindItemDefinition(ID)?.shortname ?? ID.ToString()} | {PoolUtils.GetStats<ItemData>()}");
                #endif

                ID = 0;
                Position = 0;
                Amount = 0;
                IsBlueprint = false;
                BlueprintTarget = 0;
                Skin = 0;
                Fuel = 0;
                FlameFuel = 0;
                Condition = 0;
                MaxCondition = 0;
                Ammo = 0;
                AmmoType = 0;
                DataInt = 0;
                Name = null;
                Text = null;
                Flags = 0;

                if (EntityData != null)
                {
                    var entityData = EntityData;
                    Pool.Free(ref entityData);
                    EntityData = null;
                }

                if (Contents != null)
                {
                    PoolUtils.ResetItemsAndClear(Contents);
                    var contents = Contents;
                    Pool.FreeList(ref contents);
                    Contents = null;
                }
            }

            public void LeavePool()
            {
                #if DEBUG_POOLING
                LogDebug($"ItemData::LeavePool | {PoolUtils.GetStats<ItemData>()}");
                #endif
            }

            public void Reduce(int amount)
            {
                Amount -= amount;
            }

            public Item ToItem(int amount = -1)
            {
                if (amount == -1)
                {
                    amount = Amount;
                }

                if (amount == 0)
                    return null;

                var item = ItemManager.CreateByItemID(ID, amount, Skin);
                if (item == null)
                    return null;

                item.position = Position % _maxCapacityPerPage;

                if (IsBlueprint)
                {
                    item.blueprintTarget = BlueprintTarget;
                    return item;
                }

                item.fuel = Fuel;
                item.condition = Condition;

                if (MaxCondition != -1)
                {
                    item.maxCondition = MaxCondition;
                }

                if (Name != null)
                {
                    item.name = Name;
                }

                if (amount == Amount && Contents?.Count > 0)
                {
                    if (item.contents == null)
                    {
                        item.contents = new ItemContainer();
                        item.contents.ServerInitialize(null, Contents.Count);
                        item.contents.GiveUID();
                        item.contents.parent = item;
                    }
                    else
                    {
                        item.contents.capacity = Math.Max(item.contents.capacity, Contents.Count);
                    }

                    foreach (var contentItem in Contents)
                    {
                        var childItem = contentItem.ToItem();
                        if (childItem == null)
                            continue;

                        if (!childItem.MoveToContainer(item.contents, childItem.position)
                            && !childItem.MoveToContainer(item.contents))
                        {
                            childItem.Remove();
                        }
                    }
                }

                item.flags |= Flags;

                var magazine = item.GetHeldEntity()?.GetComponent<BaseProjectile>()?.primaryMagazine;
                var flameThrower = item.GetHeldEntity()?.GetComponent<FlameThrower>();

                if (magazine != null)
                {
                    magazine.contents = Ammo;
                    magazine.ammoType = ItemManager.FindItemDefinition(AmmoType);
                }

                if (flameThrower != null)
                {
                    flameThrower.ammo = FlameFuel;
                }

                if (DataInt > 0)
                {
                    item.instanceData = new ProtoBuf.Item.InstanceData
                    {
                        ShouldPool = false,
                        dataInt = DataInt,
                    };
                }

                item.text = Text;

                EntityData?.UpdateAssociatedEntity(item);

                return item;
            }
        }

        #endregion

        #region Stored Data

        [JsonObject(MemberSerialization.OptIn)]
        private class StoredData
        {
            public static StoredData Load()
            {
                var data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(nameof(Backpacks));
                if (data == null)
                {
                    LogWarning($"Data file {nameof(Backpacks)}.json is invalid. Creating new data file.");
                    data = new StoredData { _dirty = true };
                    data.SaveIfChanged();
                }
                return data;
            }

            [JsonProperty("PlayersWithDisabledGUI")]
            private HashSet<ulong> DeprecatedPlayersWithDisabledGUI
            {
                set
                {
                    foreach (var playerId in value)
                    {
                        EnabledGuiPreference[playerId] = false;
                    }
                }
            }

            [JsonProperty("PlayerGuiPreferences")]
            private Dictionary<ulong, bool> EnabledGuiPreference = new Dictionary<ulong, bool>();

            [JsonIgnore]
            private bool _dirty;

            public bool? GetGuiButtonPreference(ulong userId)
            {
                bool guiEnabled;
                return EnabledGuiPreference.TryGetValue(userId, out guiEnabled)
                    ? guiEnabled as bool?
                    : null;
            }

            public bool ToggleGuiButtonPreference(ulong userId, bool defaultEnabled)
            {
                var enabledNow = !(GetGuiButtonPreference(userId) ?? defaultEnabled);
                EnabledGuiPreference[userId] = enabledNow;
                _dirty = true;
                return enabledNow;
            }

            public bool SaveIfChanged()
            {
                if (!_dirty)
                    return false;

                Interface.Oxide.DataFileSystem.WriteObject(nameof(Backpacks), this);
                _dirty = false;
                return true;
            }
        }

        #endregion

        #region Configuration

        [JsonObject(MemberSerialization.OptIn)]
        private abstract class BaseItemRuleset
        {
            [JsonIgnore]
            protected abstract string PermissionPrefix { get; }

            [JsonProperty("Name", Order = -2, DefaultValueHandling = DefaultValueHandling.Ignore)]
            public string Name;

            [JsonProperty("Allowed item categories")]
            public string[] AllowedItemCategoryNames = Array.Empty<string>();

            [JsonProperty("Disallowed item categories")]
            public string[] DisallowedItemCategoryNames = Array.Empty<string>();

            [JsonProperty("Allowed item short names")]
            public string[] AllowedItemShortNames = Array.Empty<string>();

            [JsonProperty("Disallowed item short names")]
            public string[] DisallowedItemShortNames = Array.Empty<string>();

            [JsonProperty("Allowed skin IDs")]
            public HashSet<ulong> AllowedSkinIds = new HashSet<ulong>();

            [JsonProperty("Disallowed skin IDs")]
            public HashSet<ulong> DisallowedSkinIds = new HashSet<ulong>();

            [JsonIgnore]
            protected ItemCategory[] _allowedItemCategories;

            [JsonIgnore]
            protected ItemCategory[] _disallowedItemCategories;

            [JsonIgnore]
            protected HashSet<int> _allowedItemIds = new HashSet<int>();

            [JsonIgnore]
            protected HashSet<int> _disallowedItemIds = new HashSet<int>();

            [JsonIgnore]
            public string Permission { get; protected set; }

            [JsonIgnore]
            public bool AllowsAll { get; protected set; }

            public void Init(Backpacks plugin)
            {
                if (!string.IsNullOrWhiteSpace(Name))
                {
                    Permission = $"{nameof(Backpacks)}.{PermissionPrefix}.{Name}".ToLower();
                    plugin.permission.RegisterPermission(Permission, plugin);
                }

                var errorFormat = "Invalid item category in config: {0}";
                _allowedItemCategories = ParseEnumList<ItemCategory>(AllowedItemCategoryNames, errorFormat);
                _disallowedItemCategories = ParseEnumList<ItemCategory>(DisallowedItemCategoryNames, errorFormat);

                foreach (var itemShortName in AllowedItemShortNames)
                {
                    var itemDefinition = ItemManager.FindItemDefinition(itemShortName);
                    if (itemDefinition != null)
                    {
                        _allowedItemIds.Add(itemDefinition.itemid);
                    }
                    else
                    {
                        LogError($"Invalid item short name in config: {itemShortName}");
                    }
                }

                foreach (var itemShortName in DisallowedItemShortNames)
                {
                    var itemDefinition = ItemManager.FindItemDefinition(itemShortName);
                    if (itemDefinition != null)
                    {
                        _disallowedItemIds.Add(itemDefinition.itemid);
                    }
                    else
                    {
                        LogError($"Invalid item short name in config: {itemShortName}");
                    }
                }

                if (_allowedItemCategories.Contains(ItemCategory.All)
                    && _disallowedItemCategories.Length == 0
                    && _disallowedItemIds.Count == 0
                    && DisallowedSkinIds.Count == 0)
                {
                    AllowsAll = true;
                }
            }

            public bool AllowsItem(Item item)
            {
                // Optimization: Skip all checks if all items are allowed.
                if (AllowsAll)
                    return true;

                if (DisallowedSkinIds.Contains(item.skin))
                    return false;

                if (AllowedSkinIds.Contains(item.skin))
                    return true;

                if (_disallowedItemIds.Contains(item.info.itemid))
                    return false;

                if (_allowedItemIds.Contains(item.info.itemid))
                    return true;

                if (_disallowedItemCategories.Contains(item.info.category))
                    return false;

                if (_allowedItemCategories.Contains(item.info.category))
                    return true;

                return _allowedItemCategories.Contains(ItemCategory.All);
            }

            public bool AllowsItem(ItemData itemData)
            {
                // Optimization: Skip all checks if all items are allowed.
                if (AllowsAll)
                    return true;

                if (DisallowedSkinIds.Contains(itemData.Skin))
                    return false;

                if (AllowedSkinIds.Contains(itemData.Skin))
                    return true;

                if (_disallowedItemIds.Contains(itemData.ID))
                    return false;

                if (_allowedItemIds.Contains(itemData.ID))
                    return true;

                // Optimization: Skip looking up the ItemDefinition if all categories are allowed.
                if (_allowedItemCategories.Contains(ItemCategory.All) && _disallowedItemCategories.Length == 0)
                    return true;

                var itemDefinition = ItemManager.FindItemDefinition(itemData.ID);
                if ((object)itemDefinition == null)
                    return true;

                if (_disallowedItemCategories.Contains(itemDefinition.category))
                    return false;

                if (_allowedItemCategories.Contains(itemDefinition.category))
                    return true;

                return _allowedItemCategories.Contains(ItemCategory.All);
            }
        }

        [JsonObject(MemberSerialization.OptIn)]
        private class RestrictionRuleset : BaseItemRuleset
        {
            private const string PartialPermissionPrefix = "restrictions";

            public static readonly string FullPermissionPrefix = $"{nameof(Backpacks)}.{PartialPermissionPrefix}".ToLower();

            public static readonly RestrictionRuleset AllowAll = new RestrictionRuleset { AllowsAll = true };

            protected override string PermissionPrefix => PartialPermissionPrefix;
        }

        [JsonObject(MemberSerialization.OptIn)]
        private class RestrictionOptions
        {
            [JsonProperty("Enabled")]
            public bool Enabled;

            [JsonProperty("Enable legacy noblacklist permission")]
            public bool EnableLegacyPermission;

            [JsonProperty("Feedback effect")]
            public string FeedbackEffect = "assets/prefabs/locks/keypad/effects/lock.code.denied.prefab";

            [JsonProperty("Default ruleset")]
            public RestrictionRuleset DefaultRuleset = new RestrictionRuleset
            {
                AllowedItemCategoryNames = new[] { ItemCategory.All.ToString() }
            };

            [JsonProperty("Rulesets by permission")]
            public RestrictionRuleset[] RulesetsByPermission =
            {
                new RestrictionRuleset
                {
                    Name = "allowall",
                    AllowedItemCategoryNames = new [] { ItemCategory.All.ToString() },
                },
            };

            [JsonIgnore]
            private Permission _permission;

            public void Init(Backpacks plugin)
            {
                _permission = plugin.permission;

                if (EnableLegacyPermission)
                {
                    _permission.RegisterPermission(LegacyNoBlacklistPermission, plugin);
                }

                DefaultRuleset.Init(plugin);

                foreach (var ruleset in RulesetsByPermission)
                {
                    ruleset.Init(plugin);
                }
            }

            public RestrictionRuleset GetForPlayer(string userIdString)
            {
                if (EnableLegacyPermission && _permission.UserHasPermission(userIdString, LegacyNoBlacklistPermission))
                    return RestrictionRuleset.AllowAll;

                for (var i = RulesetsByPermission.Length - 1; i >= 0; i--)
                {
                    var ruleset = RulesetsByPermission[i];
                    if (ruleset.Permission != null && _permission.UserHasPermission(userIdString, ruleset.Permission))
                        return ruleset;
                }

                return DefaultRuleset;
            }
        }

        [JsonObject(MemberSerialization.OptIn)]
        private class WipeRuleset : BaseItemRuleset
        {
            public static readonly WipeRuleset AllowAll = new WipeRuleset
            {
                MaxSlotsToKeep = -1,
                AllowsAll = true
            };

            [JsonIgnore]
            protected override string PermissionPrefix => "keeponwipe";

            [JsonProperty("Max slots to keep")]
            public int MaxSlotsToKeep;

            [JsonIgnore]
            public bool DisallowsAll
            {
                get
                {
                    if (AllowsAll)
                        return false;

                    if (MaxSlotsToKeep == 0)
                        return true;

                    return _allowedItemCategories.Length == 0
                       && _allowedItemIds.Count == 0
                       && AllowedSkinIds.Count == 0;
                }
            }
        }

        [JsonObject(MemberSerialization.OptIn)]
        private class WipeOptions
        {
            [JsonProperty("Enabled")]
            public bool Enabled;

            [JsonProperty("Enable legacy keeponwipe permission")]
            public bool EnableLegacyPermission;

            [JsonProperty("Default ruleset")]
            public WipeRuleset DefaultRuleset = new WipeRuleset();

            [JsonProperty("Rulesets by permission")]
            public WipeRuleset[] RulesetsByPermission =
            {
                new WipeRuleset
                {
                    Name = "all",
                    MaxSlotsToKeep = -1,
                    AllowedItemCategoryNames = new [] { ItemCategory.All.ToString() },
                },
            };

            [JsonIgnore]
            private Permission _permission;

            public void Init(Backpacks plugin)
            {
                _permission = plugin.permission;

                if (EnableLegacyPermission)
                {
                    _permission.RegisterPermission(LegacyKeepOnWipePermission, plugin);
                }

                DefaultRuleset.Init(plugin);

                foreach (var ruleset in RulesetsByPermission)
                {
                    ruleset.Init(plugin);
                }
            }

            public WipeRuleset GetForPlayer(string userIdString)
            {
                if (EnableLegacyPermission && _permission.UserHasPermission(userIdString, LegacyKeepOnWipePermission))
                    return WipeRuleset.AllowAll;

                for (var i = RulesetsByPermission.Length - 1; i >= 0; i--)
                {
                    var ruleset = RulesetsByPermission[i];
                    if (ruleset.Permission != null && _permission.UserHasPermission(userIdString, ruleset.Permission))
                        return ruleset;
                }

                return DefaultRuleset;
            }
        }

        [JsonObject(MemberSerialization.OptIn)]
        private class BackpackSizeOptions
        {
            [JsonProperty("Default size")]
            public int DefaultSize = 6;

            [JsonProperty("Max size per page")]
            public int MaxCapacityPerPage = 48;

            [JsonProperty("Enable legacy backpacks.use.1-8 row permissions")]
            public bool EnableLegacyRowPermissions;

            [JsonProperty("Permission sizes")]
            public int[] PermissionSizes = { 6, 12, 18, 24, 30, 36, 42, 48 };
        }

        [JsonObject(MemberSerialization.OptIn)]
        private class ContainerUiOptions
        {
            [JsonProperty("Show page buttons on container bar")]
            public bool ShowPageButtonsOnContainerBar;
        }

        [JsonObject(MemberSerialization.OptIn)]
        private class Configuration : BaseConfiguration
        {
            [JsonProperty("Backpack size")]
            public BackpackSizeOptions BackpackSize = new BackpackSizeOptions();

            [JsonProperty("Backpack Size (1-8 Rows)")]
            private int DeprecatedBackpackRows
            {
                set
                {
                    BackpackSize.DefaultSize = value * 6;
                    BackpackSize.EnableLegacyRowPermissions = true;
                }
            }

            [JsonProperty("Backpack Size (1-7 Rows)")]
            private int DeprecatedBackpackSize
            {
                set
                {
                    BackpackSize.DefaultSize = value * 6;
                    BackpackSize.EnableLegacyRowPermissions = true;
                }
            }

            // Backwards compatibility for 3.8+ pre-releases.
            [JsonProperty("Default Backpack Size")]
            private int DeprecatedDefaultBackpackSize { set { BackpackSize.DefaultSize = value; } }

            // Backwards compatibility for 3.8+ pre-releases.
            [JsonProperty("Max Size Per Page")]
            private int DeprecatedMaxSizePerPage { set { BackpackSize.MaxCapacityPerPage = value; } }

            // Backwards compatibility for 3.8+ pre-releases.
            [JsonProperty("Backpack Permission Sizes")]
            private int[] DeprecatedPermissionSizes { set { BackpackSize.PermissionSizes = value; } }

            // Backwards compatibility for 3.8+ pre-releases.
            [JsonProperty("Enable Legacy Row Permissions (true/false)")]
            private bool EnableLegacyRowPermissions { set { BackpackSize.EnableLegacyRowPermissions = value; } }

            [JsonProperty("Drop on Death (true/false)")]
            public bool DropOnDeath = true;

            [JsonProperty("Erase on Death (true/false)")]
            public bool EraseOnDeath = false;

            [JsonProperty("Clear Backpacks on Map-Wipe (true/false)")]
            public bool DeprecatedClearBackpacksOnWipe;

            public bool ShouldSerializeDeprecatedClearBackpacksOnWipe() => false;

            [JsonProperty("Use Blacklist (true/false)")]
            public bool DeprecatedUseDenylist;

            public bool ShouldSerializeDeprecatedUseDenylist() => false;

            [JsonProperty("Blacklisted Items (Item Shortnames)")]
            public string[] DeprecatedDenylistItemShortNames;

            public bool ShouldSerializeDeprecatedDenylistItemShortNames() => false;

            [JsonProperty("Use Whitelist (true/false)")]
            public bool DeprecatedUseAllowlist;

            public bool ShouldSerializeDeprecatedUseAllowlist() => false;

            [JsonProperty("Whitelisted Items (Item Shortnames)")]
            public string[] DeprecatedAllowedItemShortNames;

            public bool ShouldSerializeDeprecatedAllowedItemShortNames() => false;

            [JsonProperty("Minimum Despawn Time (Seconds)")]
            public float MinimumDespawnTime = 300;

            [JsonProperty("GUI Button")]
            public GUIButton GUI = new GUIButton();

            [JsonProperty("Container UI")]
            public ContainerUiOptions ContainerUi = new ContainerUiOptions();

            [JsonProperty("Softcore")]
            public SoftcoreOptions Softcore = new SoftcoreOptions();

            [JsonProperty("Item restrictions")]
            public RestrictionOptions ItemRestrictions = new RestrictionOptions();

            [JsonProperty("Clear on wipe")]
            public WipeOptions ClearOnWipe = new WipeOptions();

            public class GUIButton
            {
                [JsonProperty("Enabled")]
                public bool Enabled = true;

                [JsonProperty("Enabled by default (for players with permission)")]
                public bool EnabledByDefault = true;

                [JsonProperty("Skin Id")]
                public ulong SkinId;

                [JsonProperty("Image")]
                public string Image = "https://i.imgur.com/CyF0QNV.png";

                [JsonProperty("Background Color")]
                public string Color = "0.969 0.922 0.882 0.035";

                [JsonProperty("GUI Button Position")]
                public Position GUIButtonPosition = new Position();

                public class Position
                {
                    [JsonProperty("Anchors Min")]
                    public string AnchorsMin = "0.5 0.0";

                    [JsonProperty("Anchors Max")]
                    public string AnchorsMax = "0.5 0.0";

                    [JsonProperty("Offsets Min")]
                    public string OffsetsMin = "185 18";

                    [JsonProperty("Offsets Max")]
                    public string OffsetsMax = "245 78";
                }
            }

            public class SoftcoreOptions
            {
                [JsonProperty("Reclaim Fraction")]
                public float ReclaimFraction = 0.5f;
            }

            public void Init(Backpacks plugin)
            {
                ItemRestrictions.Init(plugin);
                ClearOnWipe.Init(plugin);
            }
        }

        private Configuration GetDefaultConfig() => new Configuration();

        #region Configuration Helpers

        [JsonObject(MemberSerialization.OptIn)]
        private class BaseConfiguration
        {
            private string ToJson() => JsonConvert.SerializeObject(this);

            public Dictionary<string, object> ToDictionary() => JsonHelper.Deserialize(ToJson()) as Dictionary<string, object>;
        }

        private static class JsonHelper
        {
            public static object Deserialize(string json) => ToObject(JToken.Parse(json));

            private static object ToObject(JToken token)
            {
                switch (token.Type)
                {
                    case JTokenType.Object:
                        return token.Children<JProperty>()
                                    .ToDictionary(prop => prop.Name,
                                                  prop => ToObject(prop.Value));

                    case JTokenType.Array:
                        return token.Select(ToObject).ToList();

                    default:
                        return ((JValue)token).Value;
                }
            }
        }

        private bool MaybeUpdateConfig(BaseConfiguration config)
        {
            var currentWithDefaults = config.ToDictionary();
            var currentRaw = Config.ToDictionary(x => x.Key, x => x.Value);
            return MaybeUpdateConfigSection(currentWithDefaults, currentRaw);
        }

        private bool MaybeUpdateConfigSection(Dictionary<string, object> currentWithDefaults, Dictionary<string, object> currentRaw)
        {
            bool changed = false;

            foreach (var key in currentWithDefaults.Keys)
            {
                object currentRawValue;
                if (currentRaw.TryGetValue(key, out currentRawValue))
                {
                    var defaultDictValue = currentWithDefaults[key] as Dictionary<string, object>;
                    var currentDictValue = currentRawValue as Dictionary<string, object>;

                    if (defaultDictValue != null)
                    {
                        if (currentDictValue == null)
                        {
                            currentRaw[key] = currentWithDefaults[key];
                            changed = true;
                        }
                        else if (MaybeUpdateConfigSection(defaultDictValue, currentDictValue))
                            changed = true;
                    }
                }
                else
                {
                    currentRaw[key] = currentWithDefaults[key];
                    changed = true;
                }
            }

            return changed;
        }

        protected override void LoadDefaultConfig() => _config = GetDefaultConfig();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<Configuration>();
                if (_config == null)
                {
                    throw new JsonException();
                }

                var changed = MaybeUpdateConfig(_config);

                if (_config.DeprecatedUseAllowlist || _config.DeprecatedUseDenylist)
                {
                    changed = true;

                    _config.ItemRestrictions.Enabled = true;
                    _config.ItemRestrictions.EnableLegacyPermission = true;

                    if (_config.DeprecatedUseAllowlist)
                    {
                        _config.ItemRestrictions.DefaultRuleset.AllowedItemCategoryNames = Array.Empty<string>();
                        _config.ItemRestrictions.DefaultRuleset.AllowedItemShortNames = _config.DeprecatedAllowedItemShortNames;
                    }
                    else if (_config.DeprecatedUseDenylist)
                    {
                        _config.ItemRestrictions.DefaultRuleset.DisallowedItemShortNames = _config.DeprecatedDenylistItemShortNames;
                    }
                }

                if (_config.DeprecatedClearBackpacksOnWipe)
                {
                    changed = true;

                    _config.ClearOnWipe.Enabled = true;
                    _config.ClearOnWipe.EnableLegacyPermission = true;
                }

                if (changed)
                {
                    PrintWarning("Configuration appears to be outdated; updating and saving");
                    SaveConfig();
                }
            }
            catch (Exception e)
            {
                PrintError(e.Message);
                PrintWarning($"Configuration file {Name}.json is invalid; using defaults");
                LoadDefaultConfig();
            }
        }

        protected override void SaveConfig()
        {
            Puts($"Configuration changes saved to {Name}.json");
            Config.WriteObject(_config, true);
        }

        #endregion

        #endregion

        #region Localization

        private string GetMessage(string playerId, string langKey) =>
            lang.GetMessage(langKey, this, playerId);

        private string GetMessage(IPlayer player, string langKey) =>
            GetMessage(player.Id, langKey);

        private string GetMessage(BasePlayer basePlayer, string langKey) =>
            GetMessage(basePlayer.UserIDString, langKey);

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["No Permission"] = "You don't have permission to use this command.",
                ["May Not Open Backpack In Event"] = "You may not open a backpack while participating in an event!",
                ["View Backpack Syntax"] = "Syntax: /viewbackpack <name or id>",
                ["User ID not Found"] = "Could not find player with ID '{0}'",
                ["User Name not Found"] = "Could not find player with name '{0}'",
                ["Multiple Players Found"] = "Multiple matching players found:\n{0}",
                ["Backpack Item Rejected"] = "That item is not allowed in the backpack.",
                ["Backpack Items Rejected"] = "Your backpack rejected some items. They have been added to your inventory or dropped.",
                ["Backpack Over Capacity"] = "Your backpack was over capacity. Overflowing items were added to your inventory or dropped.",
                ["Blacklisted Items Removed"] = "Your backpack contained blacklisted items. They have been added to your inventory or dropped.",
                ["Backpack Fetch Syntax"] = "Syntax: backpack.fetch <item short name or id> <amount>",
                ["Invalid Item"] = "Invalid Item Name or ID.",
                ["Invalid Item Amount"] = "Item amount must be an integer greater than 0.",
                ["Item Not In Backpack"] = "Item \"{0}\" not found in backpack.",
                ["Items Fetched"] = "Fetched {0} \"{1}\" from backpack.",
                ["Fetch Failed"] = "Couldn't fetch \"{0}\" from backpack. Inventory may be full.",
                ["Toggled Backpack GUI"] = "Toggled backpack GUI button.",
                ["UI - Gather All"] = "Gather: All ↓",
                ["UI - Gather Existing"] = "Gather: Existing ↓",
                ["UI - Gather Off"] = "Gather: Off",
                ["UI - Retrieve On"] = "Retrieve: On ↑",
                ["UI - Retrieve Off"] = "Retrieve: Off"
            }, this);
        }

        #endregion
    }
}
