## Features

- Adds a small stash to deployable drones (not delivery drones)
- Drops stash contents when the drone is destroyed
- Allows the drone controller to remotely view the stash contents, as well as drop them at the drone position
- Allows configuring stash capacity based on permissions of the drone owner
- Attacking the stash deals damage to the drone instead

## Installation

1. Add the plugin to the `oxide/plugins` directory of your Rust server installation
2. Update the config value for `DefaultCapacity` if you want all drones to have a stash (0 for no stash)
3. Grant permissions to players who you want to allow more storage than the default
4. Reload the plugin

All existing deployed drones, as well as those deployed while the plugin is loaded, will have a stash added to them according to the plugin config and drone owner permissions.

Note: Once drone stashes have spawned, you can increase their capacity by updating permissions and reloading the plugin, but capacity of existing drones will never be reduced to prevent hiding items.

## Permissions

The following permissions come with this plugin's **default configuration**. Granting one to a player determines the capacity of stash containers added to drones they deploy.

- `dronestorage.capacity.6` -- 1 row
- `dronestorage.capacity.18` -- 3 rows
- `dronestorage.capacity.30` -- 5 rows
- `dronestorage.capacity.42` -- 7 rows

You can add more capacity amounts in the plugin configuration (`CapacityAmountsRequiringPermission`), and the plugin will automatically generate permissions of the format `dronestorage.capacity.<amount>` when reloaded. If a player has permission to multiple capacity amounts, only the last one will apply (based on the order in the config).

## Configuration

Default configuration:

```json
{
  "DefaultCapacity": 0,
  "CapacityAmountsRequiringPermission": [
    6,
    18,
    30,
    42
  ]
}
```

## Localization

```json
{
  "UI.Button.ViewItems": "View Items",
  "UI.Button.DropItems": "Drop Items"
}
```

## Developer Hooks

#### OnDroneStorageSpawn

- Called when this plugin is about to spawn a stash container on a drone
- Returning `false` will prevent the default behavior
- Returning `null` will result in the default behavior

```csharp
object OnDroneStorageSpawn(Drone drone)
```

#### OnDroneStorageSpawned

- Called after this plugin has spawned a stash container on a drone
- No return behavior

```csharp
void OnDroneStorageSpawned(Drone drone, StashContainer stash)
```

#### OnDroneStorageDrop

- Called when a drone's stash is about to be dropped
- Returning `false` will prevent the default behavior
- Returning `null` will result in the default behavior

```csharp
object OnDroneStorageDrop(Drone drone, StashContainer stash, BasePlayer optionalPilot)
```

Note: The `BasePlayer` argument will be `null` if the container is being dropped because the drone was destroyed instead of an intentional drop.

#### OnDroneStorageDropped

- Called after a drone's stash has been dropped
- No return behavior

```csharp
void OnDroneStorageDropped(Drone drone, StashContainer stash, DroppedItemContainer dropContainer, BasePlayer optionalPilot)
```

Note: The `BasePlayer` argument will be `null` if the container is being dropped because the drone was destroyed instead of an intentional drop.
