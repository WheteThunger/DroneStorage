## Features

- Allows players to manually or automatically deploy a small stash to deployable drones (not delivery drones)
- Allows drone controllers to remotely view the stash contents, as well as drop them at the drone position
- Allows configuring stash capacity based on permissions of the drone owner
- Redirects damage from the stash to the drone
- Drops the stash contents when the drone is destroyed

## Commands

- `dronestash` -- Deploys a stash onto the drone the player is looking at, consuming a stash item from their inventory unless they have permission for free stashes.

## Permissions

### Deployment permissions

- `dronestorage.deploy` -- Required to use the `dronestash` command.
- `dronestorage.deploy.free` -- Allows using the `dronestash` command for free (no stash item required).
- `dronestorage.autodeploy` -- Drones deployed by players with this permission will automatically have a stash, free of charge.
  - Note: Reloading the plugin will automatically add stashes to existing drones owned by players with this permission.
  - Not recommended if you want to allow players to deploy other attachments such as auto turrets since they are incompatible.

Note: In order for manual or automatic deployment to work, the player will need permission to at least 1 storage capacity (see the Capacity permissions section below).

### UI Permissions

Players with the following permissions will see UI buttons while controlling a drone.

- `dronestorage.viewitems` -- Allows viewing the stash contents.
- `dronestorage.dropitems` -- Allows dropping the stash contents at the drone position.

Note: The drone controller is prevented from remotely moving items into or out of the stash container.

### Capacity permissions

The following permissions come with this plugin's **default configuration**. Granting one to a player determines the capacity of stash containers they deploy onto drones.

- `dronestorage.capacity.6` -- 1 row
- `dronestorage.capacity.12` -- 2 rows
- `dronestorage.capacity.18` -- 3 rows
- `dronestorage.capacity.24` -- 4 rows
- `dronestorage.capacity.30` -- 5 rows
- `dronestorage.capacity.36` -- 6 rows
- `dronestorage.capacity.42` -- 7 rows

You can add more capacity amounts in the plugin configuration (`CapacityAmounts`), and the plugin will automatically generate permissions of the format `dronestorage.capacity.<amount>` when reloaded. If a player has permission to multiple capacity amounts, the highest will apply.

## Configuration

Default configuration:

```json
{
  "TipChance": 25,
  "CapacityAmounts": [
    6,
    12,
    18,
    24,
    30,
    36,
    42
  ],
  "DisallowedItems": [],
  "DisallowedSkins": [],
  "UISettings": {
    "AnchorMin": "0.5 1",
    "AnchorMax": "0.5 1",
    "OffsetMin": "0 -75",
    "OffsetMax": "0 -75",
    "Buttons": {
      "Spacing": 25,
      "Width": 85,
      "Height": 26,
      "TextSize": 13,
      "ViewButtonColor": "0.44 0.54 0.26 1",
      "ViewButtonTextColor": "0.97 0.92 0.88 1",
      "DropButtonColor": "0.77 0.24 0.16 1",
      "DropButtonTextColor": "0.97 0.92 0.88 1"
    }
  }
}
```

- `TipChance` (`0` - `100`) -- Chance that a tip message will be shown to a player when they deploy a drone, informing them that they can use the `/dronestash` command. Only applies to players with the `dronestorage.deploy` permission who do not have the `dronestorage.autodeploy` permission.
- `CapacityAmounts` -- List of numbers used to generate permissions of the format `dronestorage.capacity.<amount>` (see permissions section).
- `DisallowedItems` -- List of item short names (like `"autoturret"`) to disallow in drone stashes.
- `DisallowedSkins` -- List of item skin ids (numbers) to disallow in drone stashes.
- `UISettings` -- Options to control the display of the UI.
  - Note: It is not possible to position the UI relative to the computer station UI consistently for all resolutions because modded UIs use scaling whereas the computer station UI uses the same fixed pixel offsets for all resolutions.

## Localization

```json
{
  "UI.Button.ViewItems": "View Items",
  "UI.Button.DropItems": "Drop Items",
  "Tip.DeployCommand": "Tip: Look at the drone and run <color=yellow>/dronestash</color> to deploy a stash.",
  "Error.NoPermission": "You don't have permission to do that.",
  "Error.NoDroneFound": "Error: No drone found.",
  "Error.NoStashItem": "Error: You need a stash to do that.",
  "Error.AlreadyHasStorage": "Error: That drone already has a stash.",
  "Error.IncompatibleAttachment": "Error: That drone has an incompatible attachment.",
  "Error.DeployFailed": "Error: Failed to deploy stash."
}
```

## FAQ

#### How do I get a drone?

As of this writing (March 2021), RC drones are a deployable item named `drone`, but they do not appear naturally in any loot table, nor are they craftable. However, since they are simply an item, you can use plugins to add them to loot tables, kits, GUI shops, etc. Admins can also get them with the command `inventory.give drone 1`, or spawn one in directly with `spawn drone.deployed`.

#### How do I remote-control a drone?

If a player has building privilege, they can pull out a hammer and set the ID of the drone. They can then enter that ID at a computer station and select it to start controlling the drone. Controls are `W`/`A`/`S`/`D` to move, `shift` (sprint) to go up, `ctrl` (duck) to go down, and mouse to steer.

#### How do I click the UI buttons for viewing or dropping the stash contents?

While controlling a drone, press the `Esc` key to make the cursor appear. When you want to control the drone again, simply click anywhere on the screen.

## Developer Hooks

#### OnDroneStorageDeploy

- Called when a stash container is about to be deployed onto a drone
- Returning `false` will prevent the stash container from being deployed
- Returning `null` will result in the default behavior

```csharp
bool? OnDroneStorageDeploy(Drone drone, BasePlayer optionalDeployer)
```

Note: The `BasePlayer` argument will be `null` if the container is being deployed automatically (not via the `dronestash` command).

#### OnDroneStorageDeployed

- Called after a stash container has been deployed onto a drone
- No return behavior

```csharp
void OnDroneStorageDeployed(Drone drone, StorageContainer stash, BasePlayer optionalDeployer)
```

Note: The `BasePlayer` argument will be `null` if the container was deployed automatically (not via the `dronestash` command).

#### OnDroneStorageDrop

- Called when a drone's stash contents are about to be dropped
- Returning `false` will prevent the stash contents from being dropped
- Returning `null` will result in the default behavior

```csharp
bool? OnDroneStorageDrop(Drone drone, StorageContainer stash, BasePlayer optionalPilot)
```

Note: The `BasePlayer` argument will be `null` if the stash contents are being dropped because the drone was destroyed (not via the pilot intentionally dropping it).

#### OnDroneStorageDropped

- Called after a drone's stash contents have been dropped
- No return behavior

```csharp
void OnDroneStorageDropped(Drone drone, StorageContainer stash, DroppedItemContainer dropContainer, BasePlayer optionalPilot)
```

Note: The `BasePlayer` argument will be `null` if the stash contents were dropped because the drone was destroyed (not via the pilot intentionally dropping it).
