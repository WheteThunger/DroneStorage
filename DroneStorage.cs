using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Core.Libraries.Covalence;
using Oxide.Game.Rust.Cui;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Drone Storage", "WhiteThunder", "1.3.1")]
    [Description("Allows players to deploy a small stash to RC drones.")]
    internal class DroneStorage : CovalencePlugin
    {
        #region Fields

        [PluginReference]
        private readonly Plugin DroneSettings;

        private Configuration _config;

        private const string PermissionDeploy = "dronestorage.deploy";
        private const string PermissionDeployFree = "dronestorage.deploy.free";
        private const string PermissionAutoDeploy = "dronestorage.autodeploy";
        private const string PermissionLockable = "dronestorage.lockable";
        private const string PermissionViewItems = "dronestorage.viewitems";
        private const string PermissionDropItems = "dronestorage.dropitems";
        private const string PermissionToggleLock = "dronestorage.togglelock";
        private const string PermissionCapacityPrefix = "dronestorage.capacity";

        // HAB storage is the best since it has an accurate collider, decent rendering distance and is a StorageContainer.
        private const string StoragePrefab = "assets/prefabs/deployable/hot air balloon/subents/hab_storage.prefab";
        private const string StorageDeployEffectPrefab = "assets/prefabs/deployable/small stash/effects/small-stash-deploy.prefab";
        private const string DropBagPrefab = "assets/prefabs/misc/item drop/item_drop.prefab";
        private const string LockEffectPrefab = "assets/prefabs/locks/keypad/effects/lock.code.lock.prefab";
        private const string UnlockEffectPrefab = "assets/prefabs/locks/keypad/effects/lock.code.unlock.prefab";

        private const int StashItemId = -369760990;

        private const BaseEntity.Slot StorageSlot = BaseEntity.Slot.UpperModifier;

        private const string ResizableLootPanelName = "generic_resizable";

        private static readonly Vector3 StorageLockPosition = new Vector3(0, 0, 0.21f);
        private static readonly Quaternion StorageLockRotation = Quaternion.Euler(0, 90, 0);

        private static readonly Vector3 StorageLocalPosition = new Vector3(0, 0.12f, 0);
        private static readonly Quaternion StorageLocalRotation = Quaternion.Euler(-90, 0, 0);

        private static readonly Vector3 StorageDropForwardLocation = new Vector3(0, 0, 0.7f);
        private static readonly Quaternion StorageDropRotation = Quaternion.Euler(90, 0, 0);

        private readonly object True = true;
        private readonly object False = false;

        private readonly Func<Item, int, bool> StashItemFilter;

        private readonly Dictionary<string, object> _removeInfo = new Dictionary<string, object>();
        private readonly Dictionary<string, object> _refundInfo = new Dictionary<string, object>
        {
            ["stash.small"] = new Dictionary<string, object>
            {
                ["Amount"] = 1,
            },
        };

        private readonly object[] NoteInvTakeOneArguments = { StashItemId, -1 };

        private readonly StorageCapacityManager _storageCapacityManager;
        private readonly DynamicHookHashSet<StorageContainer> _droneStorageTracker;
        private readonly DynamicHookHashSet<ulong> _remoteStashViewerTracker;
        private readonly HashSet<BasePlayer> _uiViewers = new HashSet<BasePlayer>();

        public DroneStorage()
        {
            StashItemFilter = CanStashAcceptItem;

            _storageCapacityManager = new StorageCapacityManager(this);

            _droneStorageTracker = new DynamicHookHashSet<StorageContainer>(this,
                nameof(OnEntityDeath),
                nameof(OnEntityTakeDamage),
                nameof(OnBookmarkControlStarted),
                nameof(OnBookmarkControlEnded),
                nameof(CanPickupEntity),
                nameof(OnItemDeployed)
            );

            _remoteStashViewerTracker = new DynamicHookHashSet<ulong>(this,
                nameof(CanMoveItem),
                nameof(OnItemAction)
            );
        }

        #endregion

        #region Hooks

        private void Init()
        {
            permission.RegisterPermission(PermissionDeploy, this);
            permission.RegisterPermission(PermissionDeployFree, this);
            permission.RegisterPermission(PermissionAutoDeploy, this);
            permission.RegisterPermission(PermissionLockable, this);
            permission.RegisterPermission(PermissionViewItems, this);
            permission.RegisterPermission(PermissionDropItems, this);
            permission.RegisterPermission(PermissionToggleLock, this);

            _storageCapacityManager.Init();

            _droneStorageTracker.Unsubscribe();
            _remoteStashViewerTracker.Unsubscribe();
        }

        private void Unload()
        {
            foreach (var player in _uiViewers)
            {
                UI.DestroyForPlayer(player);
            }

            foreach (var entity in BaseNetworkable.serverEntities)
            {
                var drone = entity as Drone;
                if (drone == null || !IsDroneEligible(drone))
                    continue;

                var stash = GetDroneStorage(drone);
                if (stash == null)
                    continue;

                DroneStorageComponent.RemoveFromStorage(stash);
            }
        }

        private void OnServerInitialized()
        {
            foreach (var drone in BaseNetworkable.serverEntities.OfType<Drone>().ToArray())
            {
                if (!IsDroneEligible(drone))
                    continue;

                RefreshStorage(drone);
            }

            foreach (var player in BasePlayer.activePlayerList)
            {
                ComputerStation computerStation;
                var drone = RCUtils.GetControlledEntity<Drone>(player, out computerStation);
                if (drone == null)
                    continue;

                var storage = GetDroneStorage(drone);
                if (storage == null)
                    continue;

                if (storage.inventory != null && storage.inventory == player.inventory?.loot?.containers?.FirstOrDefault())
                {
                    _remoteStashViewerTracker.Add(player.userID);
                }

                OnBookmarkControlStarted(computerStation, player, string.Empty, drone);
            }
        }

        private void OnEntityBuilt(Planner planner, GameObject go)
        {
            if (planner == null || go == null)
                return;

            var drone = go.ToBaseEntity() as Drone;
            if (drone == null || !IsDroneEligible(drone))
                return;

            var player = planner.GetOwnerPlayer();
            if (player == null)
                return;

            var drone2 = drone;
            var player2 = player;

            NextTick(() =>
            {
                // Delay this check to allow time for other plugins to deploy an entity to this slot.
                if (drone2 == null || player2 == null || drone2.GetSlot(StorageSlot) != null)
                    return;

                var capacity = _storageCapacityManager.DetermineCapacityForUser(drone2.OwnerID);
                if (capacity <= 0)
                    return;

                if (permission.UserHasPermission(player2.UserIDString, PermissionAutoDeploy))
                {
                    TryDeployStorage(drone2, capacity);
                }
                else if (permission.UserHasPermission(player2.UserIDString, PermissionDeploy)
                    && UnityEngine.Random.Range(0, 100) < _config.TipChance)
                {
                    ChatMessage(player2, Lang.TipDeployCommand);
                }
            });
        }

        private void OnEntityDeath(Drone drone)
        {
            var storage = GetDroneStorage(drone);
            if (storage == null)
                return;

            DropItems(drone, storage);
        }

        private object OnEntityTakeDamage(StorageContainer storage, HitInfo info)
        {
            Drone drone;
            if (!IsDroneStorage(storage, out drone))
                return null;

            drone.Hurt(info);
            HitNotify(drone, info);

            return True;
        }

        private void OnBookmarkControlStarted(ComputerStation computerStation, BasePlayer player, string bookmarkName, Drone drone)
        {
            if (!RCUtils.HasController(drone, player))
                return;

            if (_uiViewers.Contains(player))
                return;

            var storage = GetDroneStorage(drone);
            if (storage == null)
                return;

            UI.CreateForPlayer(this, player, storage);
            _uiViewers.Add(player);
        }

        private void OnBookmarkControlEnded(ComputerStation station, BasePlayer player, Drone drone)
        {
            if (!_uiViewers.Contains(player))
                return;

            UI.DestroyForPlayer(player);
            _uiViewers.Remove(player);

            if (_remoteStashViewerTracker.Contains(player.userID))
            {
                EndLooting(player);
            }
        }

        private object CanPickupEntity(BasePlayer player, Drone drone)
        {
            if (CanPickupInternal(player, drone))
                return null;

            ChatMessage(player, Lang.ErrorCannotPickupDroneWithItems);
            return False;
        }

        private void OnItemDeployed(Deployer deployer, StorageContainer storage, BaseLock baseLock)
        {
            if (!IsDroneStorage(storage))
                return;

            baseLock.transform.localPosition = StorageLockPosition;
            baseLock.transform.localRotation = StorageLockRotation;
            baseLock.SendNetworkUpdateImmediate();
        }

        // Prevent the drone controller from moving items while remotely viewing a drone stash.
        private object CanMoveItem(Item item, PlayerInventory playerInventory)
        {
            if (item.parent == null)
                return null;

            var player = playerInventory.baseEntity;
            if (player == null)
                return null;

            var drone = RCUtils.GetControlledEntity<Drone>(player);
            if (drone == null)
                return null;

            // For simplicity, block all item moves while the player is looting a drone stash.
            var storage = playerInventory.loot.entitySource as StorageContainer;
            if (storage != null && IsDroneStorage(storage))
                return False;

            return null;
        }

        // Prevent the drone controller from dropping items (or any item action) while remotely viewing a drone stash.
        private object OnItemAction(Item item, string text, BasePlayer player)
        {
            var drone = RCUtils.GetControlledEntity<Drone>(player);
            if (drone == null)
                return null;

            var storage = GetDroneStorage(drone);
            if (storage != null && storage == player.inventory.loot.entitySource)
                return False;

            return null;
        }

        // This hook is exposed by plugin: Remover Tool (RemoverTool).
        private Dictionary<string, object> OnRemovableEntityInfo(StorageContainer storage, BasePlayer player)
        {
            if (!IsDroneStorage(storage))
                return null;

            _removeInfo["DisplayName"] = GetMessage(player, Lang.InfoStashName);

            if (storage.pickup.enabled)
            {
                _removeInfo["Refund"] = _refundInfo;
            }
            else
            {
                _removeInfo.Remove("Refund");
            }

            return _removeInfo;
        }

        // This hook is exposed by plugin: Remover Tool (RemoverTool).
        private string canRemove(BasePlayer player, Drone drone)
        {
            if (CanPickupInternal(player, drone))
                return null;

            return GetMessage(player, Lang.ErrorCannotPickupDroneWithItems);
        }

        // This hook is exposed by plugin: Drone Settings (DroneSettings).
        private string OnDroneTypeDetermine(Drone drone)
        {
            return GetDroneStorage(drone) != null ? Name : null;
        }

        #endregion

        #region Commands

        [Command("dronestash")]
        private void DroneStashCommand(IPlayer player)
        {
            if (player.IsServer)
                return;

            if (!player.HasPermission(PermissionDeploy))
            {
                ReplyToPlayer(player, Lang.ErrorNoPermission);
                return;
            }

            var basePlayer = player.Object as BasePlayer;
            var drone = GetLookEntity(basePlayer, 3) as Drone;
            if (drone == null || !IsDroneEligible(drone))
            {
                ReplyToPlayer(player, Lang.ErrorNoDroneFound);
                return;
            }

            if (!basePlayer.CanBuild() || !basePlayer.CanBuild(drone.WorldSpaceBounds()))
            {
                ReplyToPlayer(player, Lang.ErrorBuildingBlocked);
                return;
            }

            var allowedCapacity = _storageCapacityManager.DetermineCapacityForUser(basePlayer.userID);
            if (allowedCapacity <= 0)
            {
                ReplyToPlayer(player, Lang.ErrorNoPermission);
                return;
            }

            if (GetDroneStorage(drone) != null)
            {
                ReplyToPlayer(player, Lang.ErrorAlreadyHasStorage);
                return;
            }

            if (drone.GetSlot(StorageSlot) != null)
            {
                ReplyToPlayer(player, Lang.ErrorIncompatibleAttachment);
                return;
            }

            var isFree = player.HasPermission(PermissionDeployFree);
            if (!isFree && basePlayer.inventory.FindItemByItemID(StashItemId) == null)
            {
                ReplyToPlayer(player, Lang.ErrorNoStashItem);
                return;
            }

            if (TryDeployStorage(drone, allowedCapacity, allowRefund: !isFree, deployer: basePlayer) == null)
            {
                ReplyToPlayer(player, Lang.ErrorDeployFailed);
            }
            else if (!isFree)
            {
                basePlayer.inventory.Take(null, StashItemId, 1);
                basePlayer.Command("note.inv", NoteInvTakeOneArguments);
            }
        }

        [Command("dronestorage.ui.viewitems")]
        private void UICommandViewItems(IPlayer player)
        {
            BasePlayer basePlayer;
            Drone drone;
            StorageContainer storage;
            if (!TryGetControlledStorage(player, PermissionViewItems, out basePlayer, out drone, out storage)
                || !RCUtils.HasController(drone, basePlayer))
                return;

            if (basePlayer.inventory.loot.IsLooting() && basePlayer.inventory.loot.entitySource == storage)
            {
                EndLooting(basePlayer);
                return;
            }

            var baseLock = GetLock(storage);
            var isLocked = baseLock != null && baseLock.IsLocked();

            // Temporarily unlock the container so that the player can view the contents without authorization.
            if (isLocked)
            {
                baseLock.SetFlag(BaseEntity.Flags.Locked, false, recursive: false, networkupdate: false);
            }

            // Temporarily remove the stash owner, to bypass plugins such as PreventLooting.
            var ownerId = storage.OwnerID;
            storage.OwnerID = 0;
            if (storage.PlayerOpenLoot(basePlayer, storage.panelName, doPositionChecks: false))
            {
                _remoteStashViewerTracker.Add(basePlayer.userID);
            }

            storage.OwnerID = ownerId;

            if (isLocked)
            {
                baseLock.SetFlag(BaseEntity.Flags.Locked, true, recursive: false, networkupdate: false);
            }
        }

        [Command("dronestorage.ui.dropitems")]
        private void UICommandDropItems(IPlayer player)
        {
            BasePlayer basePlayer;
            Drone drone;
            StorageContainer storage;
            if (!TryGetControlledStorage(player, PermissionDropItems, out basePlayer, out drone, out storage)
                || !RCUtils.HasController(drone, basePlayer))
                return;

            DropItems(drone, storage, basePlayer);
        }

        [Command("dronestorage.ui.togglelock")]
        private void UICommandLockStorage(IPlayer player)
        {
            BasePlayer basePlayer;
            Drone drone;
            StorageContainer storage;
            if (!TryGetControlledStorage(player, PermissionToggleLock, out basePlayer, out drone, out storage)
                || !RCUtils.HasController(drone, basePlayer))
                return;

            if (!_uiViewers.Contains(basePlayer))
                return;

            var baseLock = GetLock(storage);
            if (baseLock == null)
                return;

            var wasLocked = baseLock.IsLocked();
            baseLock.SetFlag(BaseEntity.Flags.Locked, !wasLocked);
            Effect.server.Run(wasLocked ? UnlockEffectPrefab : LockEffectPrefab, baseLock, 0, Vector3.zero, Vector3.zero);
            UI.CreateForPlayer(this, basePlayer, storage);
        }

        #endregion

        #region UI

        private static class UI
        {
            private const string Name = "DroneStorage";

            private static float GetButtonOffsetX(DroneStorage plugin, int index, int totalButtons)
            {
                var buttonSettings = plugin._config.UISettings.Buttons;
                var panelWidth = buttonSettings.Width * totalButtons + buttonSettings.Spacing * (totalButtons - 1);
                var offsetXMin = -panelWidth / 2 + (buttonSettings.Width + buttonSettings.Spacing) * index;
                return offsetXMin;
            }

            public static void CreateForPlayer(DroneStorage plugin, BasePlayer player, StorageContainer storage)
            {
                var baseLock = GetLock(storage);

                var iPlayer = player.IPlayer;
                var showViewItemsButton = iPlayer.HasPermission(PermissionViewItems);
                var showDropItemsButton = iPlayer.HasPermission(PermissionDropItems);
                var showToggleLockButton = baseLock != null && iPlayer.HasPermission(PermissionToggleLock);

                var totalButtons = Convert.ToInt32(showViewItemsButton)
                    + Convert.ToInt32(showDropItemsButton)
                    + Convert.ToInt32(showToggleLockButton);

                if (totalButtons == 0)
                    return;

                var config = plugin._config;

                var cuiElements = new CuiElementContainer
                {
                    new CuiElement
                    {
                        Parent = "Overlay",
                        Name = Name,
                        DestroyUi = Name,
                        Components =
                        {
                            new CuiRectTransformComponent
                            {
                                AnchorMin = config.UISettings.AnchorMin,
                                AnchorMax = config.UISettings.AnchorMax,
                                OffsetMin = config.UISettings.OffsetMin,
                                OffsetMax = config.UISettings.OffsetMax,
                            },
                        }
                    }
                };

                var currentButtonIndex = 0;
                var buttonSettings = config.UISettings.Buttons;

                if (showViewItemsButton)
                {
                    var offsetXMin = GetButtonOffsetX(plugin, currentButtonIndex++, totalButtons);

                    cuiElements.Add(
                        new CuiButton
                        {
                            Text =
                            {
                                Text = plugin.GetMessage(player.UserIDString, Lang.UIButtonViewItems),
                                Align = TextAnchor.MiddleCenter,
                                Color = buttonSettings.ViewButtonTextColor,
                                FontSize = buttonSettings.TextSize
                            },
                            Button =
                            {
                                Color = buttonSettings.ViewButtonColor,
                                Command = "dronestorage.ui.viewitems",
                            },
                            RectTransform =
                            {
                                AnchorMin = "0 0",
                                AnchorMax = "0 0",
                                OffsetMin = $"{offsetXMin.ToString()} 0",
                                OffsetMax = $"{(offsetXMin + buttonSettings.Width).ToString()} {buttonSettings.Height.ToString()}"
                            }
                        },
                        Name
                    );
                }

                if (showDropItemsButton)
                {
                    var offsetXMin = GetButtonOffsetX(plugin, currentButtonIndex++, totalButtons);

                    cuiElements.Add(
                        new CuiButton
                        {
                            Text =
                            {
                                Text = plugin.GetMessage(player.UserIDString, Lang.UIButtonDropItems),
                                Align = TextAnchor.MiddleCenter,
                                Color = buttonSettings.DropButtonTextColor,
                                FontSize = buttonSettings.TextSize
                            },
                            Button =
                            {
                                Color = buttonSettings.DropButtonColor,
                                Command = "dronestorage.ui.dropitems",
                            },
                            RectTransform =
                            {
                                AnchorMin = "0 0",
                                AnchorMax = "0 0",
                                OffsetMin = $"{offsetXMin.ToString()} 0",
                                OffsetMax = $"{(offsetXMin + buttonSettings.Width).ToString()} {buttonSettings.Height.ToString()}"
                            }
                        },
                        Name
                    );
                }

                if (showToggleLockButton)
                {
                    var isLocked = baseLock.IsLocked();
                    var offsetXMin = GetButtonOffsetX(plugin, currentButtonIndex++, totalButtons);

                    cuiElements.Add(new CuiButton
                    {
                        Text =
                        {
                            Text = plugin.GetMessage(player.UserIDString, isLocked ? Lang.UIButtonUnlockStorage : Lang.UIButtonLockStorage),
                            Align = TextAnchor.MiddleCenter,
                            Color = isLocked ? buttonSettings.UnlockButtonTextColor : buttonSettings.LockButtonTextColor,
                            FontSize = buttonSettings.TextSize
                        },
                        Button =
                        {
                            Color = isLocked ? buttonSettings.UnlockButtonColor : buttonSettings.LockButtonColor,
                            Command = "dronestorage.ui.togglelock",
                        },
                        RectTransform =
                        {
                            AnchorMin = "0 0",
                            AnchorMax = "0 0",
                            OffsetMin = $"{offsetXMin.ToString()} 0",
                            OffsetMax = $"{(offsetXMin + buttonSettings.Width).ToString()} {buttonSettings.Height.ToString()}"
                        }
                    }, Name);
                }

                CuiHelper.AddUi(player, cuiElements);
            }

            public static void DestroyForPlayer(BasePlayer player)
            {
                CuiHelper.DestroyUi(player, Name);
            }
        }

        #endregion

        #region Helpers

        private static class RCUtils
        {
            public static bool IsRCDrone(Drone drone)
            {
                return !(drone is DeliveryDrone);
            }

            public static bool HasController(IRemoteControllable controllable, BasePlayer player)
            {
                return controllable.ControllingViewerId?.SteamId == player.userID;
            }

            public static T GetControlledEntity<T>(BasePlayer player, out ComputerStation station) where T : class
            {
                station = player.GetMounted() as ComputerStation;
                if ((object)station == null)
                    return null;

                return station.currentlyControllingEnt.Get(serverside: true) as T;
            }

            public static T GetControlledEntity<T>(BasePlayer player) where T : class
            {
                ComputerStation station;
                return GetControlledEntity<T>(player, out station);
            }
        }

        private static bool DeployStorageWasBlocked(Drone drone, BasePlayer deployer)
        {
            var hookResult = Interface.CallHook("OnDroneStorageDeploy", drone, deployer);
            return hookResult is bool && (bool)hookResult == false;
        }

        private static bool DropStorageWasBlocked(Drone drone, StorageContainer storage, BasePlayer pilot)
        {
            var hookResult = Interface.CallHook("OnDroneStorageDrop", drone, storage, pilot);
            return hookResult is bool && (bool)hookResult == false;
        }

        private void RefreshDroneSettingsProfile(Drone drone)
        {
            DroneSettings?.Call("API_RefreshDroneProfile", drone);
        }

        private static bool IsDroneEligible(Drone drone)
        {
            return drone.skinID == 0 && RCUtils.IsRCDrone(drone);
        }

        private static bool IsDroneStorage(StorageContainer storage, out Drone drone)
        {
            drone = storage.GetParentEntity() as Drone;
            if (drone == null)
                return false;

            return storage.PrefabName == StoragePrefab;
        }

        private static bool IsDroneStorage(StorageContainer storage)
        {
            Drone drone;
            return IsDroneStorage(storage, out drone);
        }

        private static bool CanPickupInternal(BasePlayer player, Drone drone)
        {
            if (!IsDroneEligible(drone))
                return true;

            var storage = GetDroneStorage(drone);
            if (storage == null)
                return true;

            // Prevent drone pickup while it has a non-empty storage (the storage must be emptied first).
            if (storage != null && !storage.inventory.IsEmpty())
                return false;

            return true;
        }

        private static StorageContainer GetDroneStorage(Drone drone)
        {
            return drone.GetSlot(StorageSlot) as StorageContainer;
        }

        private static BaseLock GetLock(StorageContainer storage)
        {
            return storage.GetSlot(BaseEntity.Slot.Lock) as BaseLock;
        }

        private static void HitNotify(BaseEntity entity, HitInfo info)
        {
            var player = info.Initiator as BasePlayer;
            if (player == null)
                return;

            entity.ClientRPCPlayer(null, player, "HitNotify");
        }

        private static void RemoveProblemComponents(BaseEntity ent)
        {
            foreach (var meshCollider in ent.GetComponentsInChildren<MeshCollider>())
            {
                UnityEngine.Object.DestroyImmediate(meshCollider);
            }

            UnityEngine.Object.DestroyImmediate(ent.GetComponent<DestroyOnGroundMissing>());
            UnityEngine.Object.DestroyImmediate(ent.GetComponent<GroundWatch>());
        }

        private static void DropItems(Drone drone, StorageContainer storage, BasePlayer pilot = null)
        {
            var itemList = storage.inventory.itemList;
            if (itemList == null || itemList.Count <= 0)
                return;

            if (DropStorageWasBlocked(drone, storage, pilot))
                return;

            var dropPosition = pilot == null
                ? drone.transform.position
                : drone.transform.TransformPoint(StorageDropForwardLocation);

            if (pilot != null)
            {
                EndLooting(pilot);
            }

            Effect.server.Run(StorageDeployEffectPrefab, storage.transform.position);
            var dropContainer = storage.inventory.Drop(DropBagPrefab, dropPosition, storage.transform.rotation * StorageDropRotation, 0);
            Interface.Call("OnDroneStorageDropped", drone, storage, dropContainer, pilot);
        }

        private static BaseEntity GetLookEntity(BasePlayer basePlayer, float maxDistance = 3)
        {
            RaycastHit hit;
            return Physics.Raycast(basePlayer.eyes.HeadRay(), out hit, maxDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
                ? hit.GetEntity()
                : null;
        }

        private static void EndLooting(BasePlayer player)
        {
            player.EndLooting();

            // HACK: Send empty respawn information to fully close the player inventory (close the storage).
            player.ClientRPCPlayer(null, player, "OnRespawnInformation");
        }

        private bool CanStashAcceptItem(Item item, int position)
        {
            if (_config.DisallowedItems != null
                && _config.DisallowedItems.Contains(item.info.shortname))
                return false;

            if (item.skin != 0
                && _config.DisallowedSkins != null
                && _config.DisallowedSkins.Contains(item.skin))
                return false;

            return true;
        }

        private bool TryGetControlledStorage(IPlayer player, string perm, out BasePlayer basePlayer, out Drone drone, out StorageContainer storage)
        {
            basePlayer = null;
            drone = null;
            storage = null;

            if (player.IsServer || !player.HasPermission(perm))
                return false;

            basePlayer = player.Object as BasePlayer;
            drone = RCUtils.GetControlledEntity<Drone>(basePlayer);
            if (drone == null)
                return false;

            storage = GetDroneStorage(drone);
            return storage != null;
        }

        private void SetupDroneStorage(Drone drone, StorageContainer storage, int capacity)
        {
            if (!_config.AssignStorageOwnership)
            {
                storage.OwnerID = 0;
            }
            else if (storage.OwnerID == 0)
            {
                storage.OwnerID = drone.OwnerID;
            }

            var storageOwnerId = _config.AssignStorageOwnership ? storage.OwnerID : drone.OwnerID;
            storage.isLockable = storageOwnerId != 0 && permission.UserHasPermission(storageOwnerId.ToString(), PermissionLockable);

            storage.inventory.canAcceptItem = StashItemFilter;

            // Damage will be processed by the drone.
            storage.baseProtection = null;

            storage.inventory.capacity = capacity;
            storage.panelName = ResizableLootPanelName;

            DroneStorageComponent.AddToStorage(this, drone, storage);
            RefreshDroneSettingsProfile(drone);
        }

        private StorageContainer TryDeployStorage(Drone drone, int capacity, bool allowRefund = false, BasePlayer deployer = null)
        {
            if (DeployStorageWasBlocked(drone, deployer))
                return null;

            var storage = GameManager.server.CreateEntity(StoragePrefab, StorageLocalPosition, StorageLocalRotation) as StorageContainer;
            if (storage == null)
                return null;

            if (_config.AssignStorageOwnership)
            {
                storage.OwnerID = deployer?.userID ?? drone.OwnerID;
            }

            storage.SetParent(drone);
            storage.Spawn();

            SetupDroneStorage(drone, storage, capacity);
            drone.SetSlot(StorageSlot, storage);

            // This flag is used to remember whether the stash should be refundable.
            // This information is lost on restart but that's a minor concern.
            storage.pickup.enabled = allowRefund;

            Effect.server.Run(StorageDeployEffectPrefab, storage.transform.position);
            Interface.CallHook("OnDroneStorageDeployed", drone, storage, deployer);

            return storage;
        }

        private void RefreshStorage(Drone drone)
        {
            var storage = GetDroneStorage(drone);
            if (storage == null)
                return;

            // Possibly increase capacity, but do not decrease it because that could hide items.
            var capacity = Math.Max(storage.inventory.capacity, _storageCapacityManager.DetermineCapacityForUser(drone.OwnerID));
            SetupDroneStorage(drone, storage, capacity);
        }

        #endregion

        #region Drone Storage Component

        private class DroneStorageComponent : MonoBehaviour
        {
            public static void AddToStorage(DroneStorage plugin, Drone drone, StorageContainer storageContainer)
            {
                var component = storageContainer.gameObject.AddComponent<DroneStorageComponent>();
                component._plugin = plugin;
                component._drone = drone;
                component._storage = storageContainer;
                plugin._droneStorageTracker.Add(storageContainer);
            }

            public static void RemoveFromStorage(StorageContainer storageContainer)
            {
                DestroyImmediate(storageContainer.GetComponent<DroneStorageComponent>());
            }

            private DroneStorage _plugin;
            private Drone _drone;
            private StorageContainer _storage;

            // Called via `entity.SendMessage("PlayerStoppedLooting", player)` in PlayerLoot.Clear().
            private void PlayerStoppedLooting(BasePlayer looter)
            {
                _plugin._remoteStashViewerTracker.Remove(looter.userID);
            }

            private void OnDestroy()
            {
                if (_drone != null && !_drone.IsDestroyed)
                {
                    _drone.Invoke(() => _plugin.RefreshDroneSettingsProfile(_drone), 0);
                }

                _plugin._droneStorageTracker.Remove(_storage);
            }
        }

        #endregion

        #region Dynamic Hooks

        private class HookCollection
        {
            public bool IsSubscribed { get; private set; } = true;
            private readonly DroneStorage _plugin;
            private readonly string[] _hookNames;
            private readonly Func<bool> _shouldSubscribe;

            public HookCollection(DroneStorage plugin, Func<bool> shouldSubscribe, params string[] hookNames)
            {
                _plugin = plugin;
                _hookNames = hookNames;
                _shouldSubscribe = shouldSubscribe;
            }

            public void Subscribe()
            {
                foreach (var hookName in _hookNames)
                {
                    _plugin.Subscribe(hookName);
                }

                IsSubscribed = true;
            }

            public void Unsubscribe()
            {
                foreach (var hookName in _hookNames)
                {
                    _plugin.Unsubscribe(hookName);
                }

                IsSubscribed = false;
            }

            public void Refresh()
            {
                if (_shouldSubscribe())
                {
                    if (!IsSubscribed)
                    {
                        Subscribe();
                    }
                }
                else if (IsSubscribed)
                {
                    Unsubscribe();
                }
            }
        }

        private class DynamicHookHashSet<T> : HashSet<T>
        {
            private readonly HookCollection _hookCollection;

            public DynamicHookHashSet(DroneStorage plugin, params string[] hookNames)
            {
                _hookCollection = new HookCollection(plugin, () => Count > 0, hookNames);
            }

            public new bool Add(T item)
            {
                var result = base.Add(item);
                if (result)
                {
                    _hookCollection.Refresh();
                }
                return result;
            }

            public new bool Remove(T item)
            {
                var result = base.Remove(item);
                if (result)
                {
                    _hookCollection.Refresh();
                }
                return result;
            }

            public void Unsubscribe() => _hookCollection.Unsubscribe();
        }

        #endregion

        #region Configuration

        private class StorageCapacityManager
        {
            private class StorageSize
            {
                public readonly int Capacity;
                public readonly string Permission;

                public StorageSize(int capacity, string permission)
                {
                    Capacity = capacity;
                    Permission = permission;
                }
            }

            private readonly DroneStorage _plugin;
            private StorageSize[] _sortedStorageSizes;

            public StorageCapacityManager(DroneStorage plugin)
            {
                _plugin = plugin;
            }

            public void Init()
            {
                var storageSizeList = new List<StorageSize>();

                foreach (var capacity in new HashSet<int>(_plugin._config.CapacityAmounts).OrderBy(capacity => capacity))
                {
                    var storageSize = new StorageSize(capacity, $"{PermissionCapacityPrefix}.{capacity.ToString()}");
                    _plugin.permission.RegisterPermission(storageSize.Permission, _plugin);
                    storageSizeList.Add(storageSize);
                }

                _sortedStorageSizes = storageSizeList.ToArray();
            }

            public int DetermineCapacityForUser(ulong userId)
            {
                if (userId == 0)
                    return 0;

                var userIdString = userId.ToString();

                for (var i = _sortedStorageSizes.Length - 1; i >= 0; i--)
                {
                    var storageSize = _sortedStorageSizes[i];
                    if (_plugin.permission.UserHasPermission(userIdString, storageSize.Permission))
                        return storageSize.Capacity;
                }

                return 0;
            }
        }

        private class Configuration : BaseConfiguration
        {
            [JsonProperty("TipChance")]
            public int TipChance = 25;

            [JsonProperty("AssignStorageOwnership")]
            public bool AssignStorageOwnership = true;

            [JsonProperty("CapacityAmounts")]
            public int[] CapacityAmounts = { 6, 12, 18, 24, 30, 36, 42, 48 };

            [JsonProperty("DisallowedItems")]
            public string[] DisallowedItems = new string[0];

            [JsonProperty("DisallowedSkins")]
            public ulong[] DisallowedSkins = new ulong[0];

            [JsonProperty("UISettings")]
            public UISettings UISettings = new UISettings();
        }

        private class UISettings
        {
            [JsonProperty("AnchorMin")]
            public string AnchorMin = "0.5 1";

            [JsonProperty("AnchorMax")]
            public string AnchorMax = "0.5 1";

            [JsonProperty("OffsetMin")]
            public string OffsetMin = "0 -75";

            [JsonProperty("OffsetMax")]
            public string OffsetMax = "0 -75";

            [JsonProperty("Buttons")]
            public UIButtons Buttons = new UIButtons();
        }

        private class UIButtons
        {
            [JsonProperty("Spacing")]
            public int Spacing = 25;

            [JsonProperty("Width")]
            public int Width = 85;

            [JsonProperty("Height")]
            public int Height = 26;

            [JsonProperty("TextSize")]
            public int TextSize = 13;

            [JsonProperty("ViewButtonColor")]
            public string ViewButtonColor = "0.44 0.54 0.26 1";

            [JsonProperty("ViewButtonTextColor")]
            public string ViewButtonTextColor = "0.97 0.92 0.88 1";

            [JsonProperty("DropButtonColor")]
            public string DropButtonColor = "0.77 0.24 0.16 1";

            [JsonProperty("DropButtonTextColor")]
            public string DropButtonTextColor = "0.97 0.92 0.88 1";

            [JsonProperty("LockButtonColor")]
            public string LockButtonColor = "0.8 0.4 0 1";

            [JsonProperty("LockButtonTextColor")]
            public string LockButtonTextColor = "0.97 0.92 0.88 1";

            [JsonProperty("UnlockButtonColor")]
            public string UnlockButtonColor = "0.8 0.4 0 1";

            [JsonProperty("UnlockButtonTextColor")]
            public string UnlockButtonTextColor = "0.97 0.92 0.88 1";
        }

        private Configuration GetDefaultConfig() => new Configuration();

        #endregion

        #region Configuration Helpers

        private class BaseConfiguration
        {
            public string ToJson() => JsonConvert.SerializeObject(this);

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
            return MaybeUpdateConfigDict(currentWithDefaults, currentRaw);
        }

        private bool MaybeUpdateConfigDict(Dictionary<string, object> currentWithDefaults, Dictionary<string, object> currentRaw)
        {
            var changed = false;

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
                        else if (MaybeUpdateConfigDict(defaultDictValue, currentDictValue))
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

                if (MaybeUpdateConfig(_config))
                {
                    LogWarning("Configuration appears to be outdated; updating and saving");
                    SaveConfig();
                }
            }
            catch (Exception e)
            {
                LogError(e.Message);
                LogWarning($"Configuration file {Name}.json is invalid; using defaults");
                LoadDefaultConfig();
            }
        }

        protected override void SaveConfig()
        {
            Log($"Configuration changes saved to {Name}.json");
            Config.WriteObject(_config, true);
        }

        #endregion

        #region Localization

        private string GetMessage(string playerId, string messageName, params object[] args)
        {
            var message = lang.GetMessage(messageName, this, playerId);
            return args.Length > 0 ? string.Format(message, args) : message;
        }

        private string GetMessage(IPlayer player, string messageName, params object[] args) =>
            GetMessage(player.Id, messageName, args);

        private string GetMessage(BasePlayer player, string messageName, params object[] args) =>
            GetMessage(player.UserIDString, messageName, args);

        private void ReplyToPlayer(IPlayer player, string messageName, params object[] args) =>
            player.Reply(string.Format(GetMessage(player, messageName), args));

        private void ChatMessage(BasePlayer player, string messageName, params object[] args) =>
            player.ChatMessage(string.Format(GetMessage(player.UserIDString, messageName), args));

        private class Lang
        {
            public const string UIButtonViewItems = "UI.Button.ViewItems";
            public const string UIButtonDropItems = "UI.Button.DropItems";
            public const string UIButtonLockStorage = "UI.Button.LockStorage";
            public const string UIButtonUnlockStorage = "UI.Button.UnlockStorage";
            public const string TipDeployCommand = "Tip.DeployCommand";
            public const string InfoStashName = "Info.StashName";
            public const string ErrorNoPermission = "Error.NoPermission";
            public const string ErrorBuildingBlocked = "Error.BuildingBlocked";
            public const string ErrorNoDroneFound = "Error.NoDroneFound";
            public const string ErrorNoStashItem = "Error.NoStashItem";
            public const string ErrorAlreadyHasStorage = "Error.AlreadyHasStorage";
            public const string ErrorIncompatibleAttachment = "Error.IncompatibleAttachment";
            public const string ErrorDeployFailed = "Error.DeployFailed";
            public const string ErrorCannotPickupDroneWithItems = "Error.CannotPickupDroneWithItems";
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                [Lang.UIButtonViewItems] = "View Items",
                [Lang.UIButtonDropItems] = "Drop Items",
                [Lang.UIButtonLockStorage] = "Lock",
                [Lang.UIButtonUnlockStorage] = "Unlock",
                [Lang.TipDeployCommand] = "Tip: Look at the drone and run <color=yellow>/dronestash</color> to deploy a stash.",
                [Lang.InfoStashName] = "Drone Stash",
                [Lang.ErrorNoPermission] = "You don't have permission to do that.",
                [Lang.ErrorBuildingBlocked] = "Error: Cannot do that while building blocked.",
                [Lang.ErrorNoDroneFound] = "Error: No drone found.",
                [Lang.ErrorNoStashItem] = "Error: You need a stash to do that.",
                [Lang.ErrorAlreadyHasStorage] = "Error: That drone already has a stash.",
                [Lang.ErrorIncompatibleAttachment] = "Error: That drone has an incompatible attachment.",
                [Lang.ErrorDeployFailed] = "Error: Failed to deploy stash.",
                [Lang.ErrorCannotPickupDroneWithItems] = "Cannot pick up that drone while its stash contains items.",
            }, this, "en");
        }

        #endregion
    }
}
