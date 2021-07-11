using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
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
    [Info("Drone Storage", "WhiteThunder", "1.0.2")]
    [Description("Allows players to deploy a small stash to RC drones.")]
    internal class DroneStorage : CovalencePlugin
    {
        #region Fields

        [PluginReference]
        private Plugin DroneSettings;

        private static DroneStorage _pluginInstance;
        private static Configuration _pluginConfig;

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

        private const string MaximumCapacityPanelName = "genericlarge";
        private const int MaximumCapacity = 42;

        private static readonly Dictionary<string, int> DisplayCapacityByPanelName = new Dictionary<string, int>
        {
            ["fuelsmall"] = 1,
            ["smallstash"] = 6,
            ["smallwoodbox"] = 12,
            ["largewoodbox"] = 30,
            ["generic"] = 36,
            [MaximumCapacityPanelName] = MaximumCapacity,
        };

        private static readonly Vector3 StorageLockPosition = new Vector3(0, 0, 0.21f);
        private static readonly Quaternion StorageLockRotation = Quaternion.Euler(0, 90, 0);

        private static readonly Vector3 StorageLocalPosition = new Vector3(0, 0.12f, 0);
        private static readonly Quaternion StorageLocalRotation = Quaternion.Euler(-90, 0, 0);

        private static readonly Vector3 StorageDropForwardLocation = new Vector3(0, 0, 0.7f);
        private static readonly Quaternion StorageDropRotation = Quaternion.Euler(90, 0, 0);

        // Subscribe to these hooks while there are storage drones.
        private DynamicHookSubscriber<uint> _storageDroneTracker = new DynamicHookSubscriber<uint>(
            nameof(OnEntityDeath),
            nameof(OnEntityTakeDamage),
            nameof(OnBookmarkControlStarted),
            nameof(OnBookmarkControlEnded),
            nameof(CanPickupEntity),
            nameof(CanAcceptItem),
            nameof(OnItemDeployed)
        );

        // Subscribe to these hooks while storage drones are being controlled.
        private DynamicHookSubscriber<ulong> _droneControllerTracker = new DynamicHookSubscriber<ulong>(
            nameof(CanMoveItem),
            nameof(OnItemAction)
        );

        #endregion

        #region Hooks

        private void Init()
        {
            _pluginInstance = this;

            permission.RegisterPermission(PermissionDeploy, this);
            permission.RegisterPermission(PermissionDeployFree, this);
            permission.RegisterPermission(PermissionAutoDeploy, this);
            permission.RegisterPermission(PermissionLockable, this);
            permission.RegisterPermission(PermissionViewItems, this);
            permission.RegisterPermission(PermissionDropItems, this);
            permission.RegisterPermission(PermissionToggleLock, this);

            foreach (var capacityAmount in _pluginConfig.CapacityAmounts)
                permission.RegisterPermission(GetCapacityPermission(capacityAmount), this);

            _storageDroneTracker.UnsubscribeAll();
            _droneControllerTracker.UnsubscribeAll();

            Unsubscribe(nameof(OnEntitySpawned));

            if (_pluginConfig.TipChance <= 0)
                Unsubscribe(nameof(OnEntityBuilt));
        }

        private void Unload()
        {
            UI.DestroyForAllPlayers();

            _pluginInstance = null;
            _pluginConfig = null;
        }

        private void OnServerInitialized()
        {
            foreach (var entity in BaseNetworkable.serverEntities)
            {
                var drone = entity as Drone;
                if (drone == null || !IsDroneEligible(drone))
                    continue;

                AddOrUpdateStorage(drone);
            }

            foreach (var player in BasePlayer.activePlayerList)
            {
                ComputerStation computerStation;
                var drone = GetControlledDrone(player, out computerStation);
                if (drone == null || GetDroneStorage(drone) == null)
                    continue;

                OnBookmarkControlStarted(computerStation, player, string.Empty, drone);
            }

            Subscribe(nameof(OnEntitySpawned));
        }

        private void OnEntitySpawned(Drone drone)
        {
            if (!IsDroneEligible(drone))
                return;

            // Delay to give other plugins an opportunity to deploy an attachment,
            // or to save the drone id to block attachments.
            NextTick(() =>
            {
                if (drone == null)
                    return;

                TryAutoDeployStorage(drone);
            });
        }

        private void OnEntityKill(Drone drone)
        {
            var storage = GetDroneStorage(drone);
            if (storage == null)
                return;

            _storageDroneTracker.Remove(drone.net.ID);
        }

        private void OnEntityKill(StorageContainer storage)
        {
            Drone drone;
            if (!IsDroneStorage(storage, out drone))
                return;

            _storageDroneTracker.Remove(drone.net.ID);
            drone.Invoke(() => RefreshDronSettingsProfile(drone), 0);
        }

        private void OnEntityDeath(Drone drone)
        {
            var storage = GetDroneStorage(drone);
            if (storage == null)
                return;

            DropItems(drone, storage);
        }

        private bool? OnEntityTakeDamage(StorageContainer storage, HitInfo info)
        {
            Drone drone;
            if (!IsDroneStorage(storage, out drone))
                return null;

            drone.Hurt(info);
            HitNotify(drone, info);

            return true;
        }

        private void OnBookmarkControlStarted(ComputerStation computerStation, BasePlayer player, string bookmarkName, Drone drone)
        {
            var storage = GetDroneStorage(drone);
            if (storage == null)
                return;

            UI.CreateForPlayer(player, storage);
            _droneControllerTracker.Add(player.userID);
        }

        private void OnBookmarkControlEnded(ComputerStation station, BasePlayer player, Drone drone)
        {
            EndLooting(player);
            UI.DestroyForPlayer(player);
            _droneControllerTracker.Remove(player.userID);
        }

        private bool? CanPickupEntity(BasePlayer player, Drone drone)
        {
            if (!IsDroneEligible(drone))
                return null;

            var storage = GetDroneStorage(drone);
            if (storage == null)
                return null;

            // Prevent drone pickup while the storage is not empty.
            if (!storage.inventory.IsEmpty())
                return false;

            return null;
        }

        private void OnEntityBuilt(Planner planner, GameObject go)
        {
            if (planner == null || go == null)
                return;

            var drone = go.ToBaseEntity() as Drone;
            if (drone == null)
                return;

            var player = planner.GetOwnerPlayer();
            if (player == null)
                return;

            NextTick(() =>
            {
                // Delay this check to allow time for other plugins to deploy an entity to this slot.
                if (drone == null || player == null || drone.GetSlot(StorageSlot) != null)
                    return;

                if (permission.UserHasPermission(player.UserIDString, PermissionDeploy)
                    && !permission.UserHasPermission(player.UserIDString, PermissionAutoDeploy)
                    && GetPlayerAllowedCapacity(player.userID) > 0
                    && UnityEngine.Random.Range(0, 100) < _pluginConfig.TipChance)
                {
                    ChatMessage(player, Lang.TipDeployCommand);
                }
            });
        }

        private ItemContainer.CanAcceptResult? CanAcceptItem(ItemContainer container, Item item)
        {
            var storage = container.entityOwner as StorageContainer;
            if (storage == null)
                return null;

            if (!IsDroneStorage(storage))
                return null;

            if (_pluginConfig.DisallowedItems != null
                && _pluginConfig.DisallowedItems.Contains(item.info.shortname))
                return ItemContainer.CanAcceptResult.CannotAccept;

            if (item.skin != 0
                && _pluginConfig.DisallowedSkins != null
                && _pluginConfig.DisallowedSkins.Contains(item.skin))
                return ItemContainer.CanAcceptResult.CannotAccept;

            return null;
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
        private bool? CanMoveItem(Item item, PlayerInventory playerInventory)
        {
            if (item.parent == null)
                return null;

            var player = playerInventory.baseEntity;
            if (player == null)
                return null;

            var drone = GetControlledDrone(player);
            if (drone == null)
                return null;

            // For simplicity, block all item moves while the player is looting a drone stash.
            var storage = playerInventory.loot.entitySource as StorageContainer;
            if (storage != null && IsDroneStorage(storage))
                return false;

            return null;
        }

        // Prevent the drone controller from dropping items (or any item action) while remotely viewing a drone stash.
        private bool? OnItemAction(Item item, string text, BasePlayer player)
        {
            var drone = GetControlledDrone(player);
            if (drone == null)
                return null;

            var storage = GetDroneStorage(drone);
            if (storage != null && storage == player.inventory.loot.entitySource)
                return false;

            return null;
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

            var allowedCapacity = GetPlayerAllowedCapacity(basePlayer.userID);
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
            if (!isFree && basePlayer.inventory.FindItemID(StashItemId) == null)
            {
                ReplyToPlayer(player, Lang.ErrorNoStashItem);
                return;
            }

            if (TryDeployStorage(drone, allowedCapacity, basePlayer) == null)
            {
                ReplyToPlayer(player, Lang.ErrorDeployFailed);
            }
            else if (!isFree)
            {
                basePlayer.inventory.Take(null, StashItemId, 1);
                basePlayer.Command("note.inv", StashItemId, -1);
            }
        }

        [Command("dronestorage.ui.viewitems")]
        private void UICommandViewItems(IPlayer player)
        {
            BasePlayer basePlayer;
            Drone drone;
            StorageContainer storage;
            if (!TryGetControlledStorage(player, PermissionViewItems, out basePlayer, out drone, out storage))
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
                baseLock.SetFlag(BaseEntity.Flags.Locked, false, recursive: false, networkupdate: false);

            storage.PlayerOpenLoot(basePlayer, storage.panelName, doPositionChecks: false);

            if (isLocked)
                baseLock.SetFlag(BasePlayer.Flags.Locked, true, recursive: false, networkupdate: false);
        }

        [Command("dronestorage.ui.dropitems")]
        private void UICommandDropItems(IPlayer player)
        {
            BasePlayer basePlayer;
            Drone drone;
            StorageContainer storage;
            if (!TryGetControlledStorage(player, PermissionDropItems, out basePlayer, out drone, out storage))
                return;

            DropItems(drone, storage, basePlayer);
        }

        [Command("dronestorage.ui.togglelock")]
        private void UICommandLockStorage(IPlayer player)
        {
            BasePlayer basePlayer;
            Drone drone;
            StorageContainer storage;
            if (!TryGetControlledStorage(player, PermissionToggleLock, out basePlayer, out drone, out storage))
                return;

            var baseLock = GetLock(storage);
            if (baseLock == null)
                return;

            var wasLocked = baseLock.IsLocked();
            baseLock.SetFlag(BaseEntity.Flags.Locked, !wasLocked);
            Effect.server.Run(wasLocked ? UnlockEffectPrefab : LockEffectPrefab, baseLock, 0, Vector3.zero, Vector3.zero);
            UI.CreateForPlayer(basePlayer, storage);
        }

        #endregion

        #region UI

        private static class UI
        {
            private const string Name = "DroneStorage";

            private static UISettings _uiSettings => _pluginConfig.UISettings;
            private static UIButtons _buttonSettings => _uiSettings.Buttons;

            private static float GetButtonOffsetX(int index, int totalButtons)
            {
                var panelWidth = _buttonSettings.Width * totalButtons + _buttonSettings.Spacing * (totalButtons - 1);
                var offsetXMin = -panelWidth / 2 + (_buttonSettings.Width + _buttonSettings.Spacing) * index;
                return offsetXMin;
            }

            public static void CreateForPlayer(BasePlayer player, StorageContainer storage)
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

                DestroyForPlayer(player);

                var cuiElements = new CuiElementContainer
                {
                    {
                        new CuiPanel
                        {
                            RectTransform =
                            {
                                AnchorMin = _pluginConfig.UISettings.AnchorMin,
                                AnchorMax = _pluginConfig.UISettings.AnchorMax,
                                OffsetMin = _pluginConfig.UISettings.OffsetMin,
                                OffsetMax = _pluginConfig.UISettings.OffsetMax
                            }
                        },
                        "Overlay",
                        Name
                    }
                };

                var currentButtonIndex = 0;

                if (showViewItemsButton)
                {
                    var offsetXMin = GetButtonOffsetX(currentButtonIndex++, totalButtons);

                    cuiElements.Add(
                        new CuiButton
                        {
                            Text =
                            {
                                Text = _pluginInstance.GetMessage(player.UserIDString, Lang.UIButtonViewItems),
                                Align = TextAnchor.MiddleCenter,
                                Color = _buttonSettings.ViewButtonTextColor,
                                FontSize = _buttonSettings.TextSize
                            },
                            Button =
                            {
                                Color = _buttonSettings.ViewButtonColor,
                                Command = "dronestorage.ui.viewitems",
                            },
                            RectTransform =
                            {
                                AnchorMin = "0 0",
                                AnchorMax = "0 0",
                                OffsetMin = $"{offsetXMin} 0",
                                OffsetMax = $"{offsetXMin + _buttonSettings.Width} {_buttonSettings.Height}"
                            }
                        },
                        Name
                    );
                }

                if (showDropItemsButton)
                {
                    var offsetXMin = GetButtonOffsetX(currentButtonIndex++, totalButtons);

                    cuiElements.Add(
                        new CuiButton
                        {
                            Text =
                            {
                                Text = _pluginInstance.GetMessage(player.UserIDString, Lang.UIButtonDropItems),
                                Align = TextAnchor.MiddleCenter,
                                Color = _buttonSettings.DropButtonTextColor,
                                FontSize = _buttonSettings.TextSize
                            },
                            Button =
                            {
                                Color = _buttonSettings.DropButtonColor,
                                Command = "dronestorage.ui.dropitems",
                            },
                            RectTransform =
                            {
                                AnchorMin = "0 0",
                                AnchorMax = "0 0",
                                OffsetMin = $"{offsetXMin} 0",
                                OffsetMax = $"{offsetXMin + _buttonSettings.Width} {_buttonSettings.Height}"
                            }
                        },
                        Name
                    );
                }

                if (showToggleLockButton)
                {
                    var isLocked = baseLock.IsLocked();
                    var offsetXMin = GetButtonOffsetX(currentButtonIndex++, totalButtons);

                    cuiElements.Add(
                        new CuiButton
                        {
                            Text =
                            {
                                Text = _pluginInstance.GetMessage(player.UserIDString, isLocked ? Lang.UIButtonUnlockStorage : Lang.UIButtonLockStorage),
                                Align = TextAnchor.MiddleCenter,
                                Color = isLocked ? _buttonSettings.UnlockButtonTextColor : _buttonSettings.LockButtonTextColor,
                                FontSize = _buttonSettings.TextSize
                            },
                            Button =
                            {
                                Color = isLocked ? _buttonSettings.UnlockButtonColor : _buttonSettings.LockButtonColor,
                                Command = "dronestorage.ui.togglelock",
                            },
                            RectTransform =
                            {
                                AnchorMin = "0 0",
                                AnchorMax = "0 0",
                                OffsetMin = $"{offsetXMin} 0",
                                OffsetMax = $"{offsetXMin + _buttonSettings.Width} {_buttonSettings.Height}"
                            }
                        },
                        Name
                    );
                }

                CuiHelper.AddUi(player, cuiElements);
            }

            public static void DestroyForPlayer(BasePlayer player)
            {
                CuiHelper.DestroyUi(player, Name);
            }

            public static void DestroyForAllPlayers()
            {
                foreach (var player in BasePlayer.activePlayerList)
                    DestroyForPlayer(player);
            }
        }

        #endregion

        #region Helper Methods

        private static bool DeployStorageWasBlocked(Drone drone, BasePlayer deployer)
        {
            object hookResult = Interface.CallHook("OnDroneStorageDeploy", drone, deployer);
            return hookResult is bool && (bool)hookResult == false;
        }

        private static bool DropStorageWasBlocked(Drone drone, StorageContainer storage, BasePlayer pilot)
        {
            object hookResult = Interface.CallHook("OnDroneStorageDrop", drone, storage, pilot);
            return hookResult is bool && (bool)hookResult == false;
        }

        private void RefreshDronSettingsProfile(Drone drone)
        {
            DroneSettings?.Call("API_RefreshDroneProfile", drone);
        }

        private static string GetCapacityPermission(int capacity) =>
            $"{PermissionCapacityPrefix}.{capacity}";

        private static bool IsDroneEligible(Drone drone) =>
            !(drone is DeliveryDrone);

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

        private static Drone GetControlledDrone(ComputerStation computerStation) =>
            computerStation.currentlyControllingEnt.Get(serverside: true) as Drone;

        private static Drone GetControlledDrone(BasePlayer player, out ComputerStation computerStation)
        {
            computerStation = player.GetMounted() as ComputerStation;
            if (computerStation == null)
                return null;

            return GetControlledDrone(computerStation);
        }

        private static Drone GetControlledDrone(BasePlayer player)
        {
            ComputerStation computerStation;
            return GetControlledDrone(player, out computerStation);
        }

        private static StorageContainer GetDroneStorage(Drone drone)
        {
            foreach (var child in drone.children)
            {
                var storage = child as StorageContainer;
                if (storage != null && storage.PrefabName == StoragePrefab)
                    return storage;
            }

            return null;
        }

        private static BaseLock GetLock(StorageContainer storage) =>
            storage.GetSlot(BaseEntity.Slot.Lock) as BaseLock;

        private static void HitNotify(BaseEntity entity, HitInfo info)
        {
            var player = info.Initiator as BasePlayer;
            if (player == null)
                return;

            entity.ClientRPCPlayer(null, player, "HitNotify");
        }

        private static string GetSmallestPanelForCapacity(int capacity)
        {
            string panelName = MaximumCapacityPanelName;
            int displayCapacity = MaximumCapacity;

            foreach (var entry in DisplayCapacityByPanelName)
            {
                if (entry.Value >= capacity && entry.Value < displayCapacity)
                {
                    panelName = entry.Key;
                    displayCapacity = entry.Value;
                }
            }

            return panelName;
        }

        private static void RemoveProblemComponents(BaseEntity ent)
        {
            foreach (var meshCollider in ent.GetComponentsInChildren<MeshCollider>())
                UnityEngine.Object.DestroyImmediate(meshCollider);

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
                EndLooting(pilot);

            Effect.server.Run(StorageDeployEffectPrefab, storage.transform.position);
            var dropContainer = storage.inventory.Drop(DropBagPrefab, dropPosition, storage.transform.rotation * StorageDropRotation);
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

        private bool TryGetControlledStorage(IPlayer player, string perm, out BasePlayer basePlayer, out Drone drone, out StorageContainer storage)
        {
            basePlayer = null;
            drone = null;
            storage = null;

            if (player.IsServer || !player.HasPermission(perm))
                return false;

            basePlayer = player.Object as BasePlayer;
            drone = GetControlledDrone(basePlayer);
            if (drone == null)
                return false;

            storage = GetDroneStorage(drone);
            return storage != null;
        }

        private bool IsStorageLockable(StorageContainer storage) =>
            storage.OwnerID != 0
            && permission.UserHasPermission(storage.OwnerID.ToString(), PermissionLockable);

        private void SetupDroneStorage(Drone drone, StorageContainer storage, int capacity)
        {
            if (storage.OwnerID == 0)
                storage.OwnerID = drone.OwnerID;

            storage.isLockable = IsStorageLockable(storage);

            // Damage will be processed by the drone.
            storage.baseProtection = null;

            storage.inventory.capacity = capacity;
            storage.panelName = GetSmallestPanelForCapacity(capacity);

            RefreshDronSettingsProfile(drone);
            _storageDroneTracker.Add(drone.net.ID);
        }

        private StorageContainer TryDeployStorage(Drone drone, int capacity, BasePlayer deployer = null)
        {
            if (DeployStorageWasBlocked(drone, deployer))
                return null;

            var storage = GameManager.server.CreateEntity(StoragePrefab, StorageLocalPosition, StorageLocalRotation) as StorageContainer;
            if (storage == null)
                return null;

            storage.OwnerID = deployer?.userID ?? drone.OwnerID;
            storage.SetParent(drone);
            storage.Spawn();

            SetupDroneStorage(drone, storage, capacity);
            drone.SetSlot(StorageSlot, storage);

            Effect.server.Run(StorageDeployEffectPrefab, storage.transform.position);
            Interface.CallHook("OnDroneStorageDeployed", drone, storage, deployer);

            return storage;
        }

        private void TryAutoDeployStorage(Drone drone)
        {
            if (drone.OwnerID == 0
                || drone.GetSlot(StorageSlot) != null
                || !permission.UserHasPermission(drone.OwnerID.ToString(), PermissionAutoDeploy))
                return;

            var capacity = GetPlayerAllowedCapacity(drone.OwnerID);
            if (capacity <= 0)
                return;

            TryDeployStorage(drone, capacity);
        }

        private void AddOrUpdateStorage(Drone drone)
        {
            var storage = GetDroneStorage(drone);
            if (storage == null)
            {
                TryAutoDeployStorage(drone);
                return;
            }

            // Possibly increase capacity, but do not decrease it because that could hide items.
            int capacity = Math.Max(storage.inventory.capacity, GetPlayerAllowedCapacity(drone.OwnerID));
            SetupDroneStorage(drone, storage, capacity);
        }

        #endregion

        #region Dynamic Hook Subscriptions

        private class DynamicHookSubscriber<T>
        {
            private HashSet<T> _list = new HashSet<T>();
            private string[] _hookNames;

            public DynamicHookSubscriber(params string[] hookNames)
            {
                _hookNames = hookNames;
            }

            public void Add(T item)
            {
                if (_list.Add(item) && _list.Count == 1)
                    SubscribeAll();
            }

            public void Remove(T item)
            {
                if (_list.Remove(item) && _list.Count == 0)
                    UnsubscribeAll();
            }

            public void SubscribeAll()
            {
                foreach (var hookName in _hookNames)
                    _pluginInstance.Subscribe(hookName);
            }

            public void UnsubscribeAll()
            {
                foreach (var hookName in _hookNames)
                    _pluginInstance.Unsubscribe(hookName);
            }
        }

        #endregion

        #region Configuration

        private int GetPlayerAllowedCapacity(ulong userId)
        {
            var capacityAmounts = _pluginConfig.CapacityAmounts;

            if (userId == 0 || capacityAmounts == null || capacityAmounts.Length == 0)
                return 0;

            var userIdString = userId.ToString();
            var largestAllowedCapacity = 0;

            for (var i = capacityAmounts.Length - 1; i >= 0; i--)
            {
                var capacity = capacityAmounts[i];
                if (capacity > largestAllowedCapacity
                    && permission.UserHasPermission(userIdString, GetCapacityPermission(capacity)))
                    largestAllowedCapacity = capacity;
            }

            return largestAllowedCapacity;
        }

        private class Configuration : SerializableConfiguration
        {
            [JsonProperty("TipChance")]
            public int TipChance = 25;

            [JsonProperty("CapacityAmounts")]
            public int[] CapacityAmounts = new int[] { 6, 12, 18, 24, 30, 36, 42 };

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
            public string LockButtonColor = "0.7 0.3 0 1";

            [JsonProperty("LockButtonTextColor")]
            public string LockButtonTextColor = "0.97 0.92 0.88 1";

            [JsonProperty("UnlockButtonColor")]
            public string UnlockButtonColor = "0.7 0.3 0 1";

            [JsonProperty("UnlockButtonTextColor")]
            public string UnlockButtonTextColor = "0.97 0.92 0.88 1";
        }

        private Configuration GetDefaultConfig() => new Configuration();

        #endregion

        #region Configuration Boilerplate

        private class SerializableConfiguration
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

        private bool MaybeUpdateConfig(SerializableConfiguration config)
        {
            var currentWithDefaults = config.ToDictionary();
            var currentRaw = Config.ToDictionary(x => x.Key, x => x.Value);
            return MaybeUpdateConfigDict(currentWithDefaults, currentRaw);
        }

        private bool MaybeUpdateConfigDict(Dictionary<string, object> currentWithDefaults, Dictionary<string, object> currentRaw)
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

        protected override void LoadDefaultConfig() => _pluginConfig = GetDefaultConfig();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _pluginConfig = Config.ReadObject<Configuration>();
                if (_pluginConfig == null)
                {
                    throw new JsonException();
                }

                if (MaybeUpdateConfig(_pluginConfig))
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
            Config.WriteObject(_pluginConfig, true);
        }

        #endregion

        #region Localization

        private void ReplyToPlayer(IPlayer player, string messageName, params object[] args) =>
            player.Reply(string.Format(GetMessage(player, messageName), args));

        private void ChatMessage(BasePlayer player, string messageName, params object[] args) =>
            player.ChatMessage(string.Format(GetMessage(player.IPlayer, messageName), args));

        private string GetMessage(IPlayer player, string messageName, params object[] args) =>
            GetMessage(player.Id, messageName, args);

        private string GetMessage(string playerId, string messageName, params object[] args)
        {
            var message = lang.GetMessage(messageName, this, playerId);
            return args.Length > 0 ? string.Format(message, args) : message;
        }

        private class Lang
        {
            public const string UIButtonViewItems = "UI.Button.ViewItems";
            public const string UIButtonDropItems = "UI.Button.DropItems";
            public const string UIButtonLockStorage = "UI.Button.LockStorage";
            public const string UIButtonUnlockStorage = "UI.Button.UnlockStorage";
            public const string TipDeployCommand = "Tip.DeployCommand";
            public const string ErrorNoPermission = "Error.NoPermission";
            public const string ErrorBuildingBlocked = "Error.BuildingBlocked";
            public const string ErrorNoDroneFound = "Error.NoDroneFound";
            public const string ErrorNoStashItem = "Error.NoStashItem";
            public const string ErrorAlreadyHasStorage = "Error.AlreadyHasStorage";
            public const string ErrorIncompatibleAttachment = "Error.IncompatibleAttachment";
            public const string ErrorDeployFailed = "Error.DeployFailed";
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
                [Lang.ErrorNoPermission] = "You don't have permission to do that.",
                [Lang.ErrorBuildingBlocked] = "Error: Cannot do that while building blocked.",
                [Lang.ErrorNoDroneFound] = "Error: No drone found.",
                [Lang.ErrorNoStashItem] = "Error: You need a stash to do that.",
                [Lang.ErrorAlreadyHasStorage] = "Error: That drone already has a stash.",
                [Lang.ErrorIncompatibleAttachment] = "Error: That drone has an incompatible attachment.",
                [Lang.ErrorDeployFailed] = "Error: Failed to deploy stash.",
            }, this, "en");
        }

        #endregion
    }
}
