# Mount Manager

Can be used to easily add mounts into the game. Will automatically add config options to your mod and sync the
configuration from a server, if the mod is installed on the server as well.

## How to add mounts

Copy the asset bundle into your project and make sure to set it as an EmbeddedResource in the properties of the asset
bundle. Default path for the asset bundle is an `assets` directory, but you can override this. This way, you don't have
to distribute your assets with your mod. They will be embedded into your mods DLL.

### Merging the DLLs into your mod

Download the MountManager.dll and the ServerSync.dll from the release section to the right. Including the DLLs is best
done via ILRepack (https://github.com/ravibpatel/ILRepack.Lib.MSBuild.Task). You can load this package (
ILRepack.Lib.MSBuild.Task) from NuGet.

If you have installed ILRepack via NuGet, simply create a file named `ILRepack.targets` in your project and copy the
following content into the file

```xml
<?xml version="1.0" encoding="utf-8"?>
<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
    <Target Name="ILRepacker" AfterTargets="Build">
        <ItemGroup>
            <InputAssemblies Include="$(TargetPath)"/>
            <InputAssemblies Include="$(OutputPath)\MountManager.dll"/>
            <InputAssemblies Include="$(OutputPath)\ServerSync.dll"/>
        </ItemGroup>
        <ILRepack Parallel="true" DebugInfo="true" Internalize="true" InputAssemblies="@(InputAssemblies)"
                  OutputFile="$(TargetPath)" TargetKind="SameAsPrimaryAssembly" LibraryPath="$(OutputPath)"/>
    </Target>
</Project>
```

Make sure to set the MountManager.dll and the ServerSync.dll in your project to "Copy to output directory" in the
properties of the DLLs and to add a reference to it. After that, simply add `using MountManager;` to your mod and use
the `Mount` class, to add your items. Please note you must be using C# 9.0 or higher for this to work. If you are
not, `using MountManager;` will cause your mod to have errors. You will need to wrap your code the old language way, or
upgrade.

As it currently stands, the mounts are summoned using an item. You must be using
the [Item Manager](https://github.com/blaxxun-boop/ItemManager) to register your items, or register them yourself. Get
the DLL from the release tab on the right of the page, and follow the same instructions listed on the link's page to
integrate it into your mod.

## You will need:

1. A prefab for the mount
2. A prefab for the mount's item
3. A prefab for the mount's saddle
4. A prefab for the summoning effect you wish to use. This effect is played when the mount is summoned.
5. A mount with GameObjects in the following structure and components:

   Note: The mount must have the `Visual` GameObject in the heirarchy. This is a requirement for Valheim.

   [![Mount Structure](https://i.imgur.com/juax58s.png)](https://i.imgur.com/juax58s.png)
   [![Mount Components](https://i.imgur.com/brJLmIQ.png)](https://i.imgur.com/brJLmIQ.png)

## Example project

This example adds two different mounts from two different asset bundles. Please note: This is not complete code for a
plugin but is the start of what you'd typically setup in your Awake method/Unity Event. See the MountManagerModTemplate
for a full project in which you can clone. The `azumatt_testing` asset bundle is in a
directory
called `MyMounts`, while the `horses` asset bundle is in a directory called `assets`. The directory `assets` is the
default and can be omitted when declaring a mount. This will be reflected in the code below for all mounts registered
from the `horses` asset bundle. Reminder, this assumes you
are using the ItemManager and MountManager DLLs or classes to register your summon item and mount.

```csharp
using System.IO;
using BepInEx;
using HarmonyLib;
using MountManager;

namespace MountManagerExampleMod
{
    [BepInPlugin(ModGUID, ModName, ModVersion)]
    public class MountManagerExampleMod : BaseUnityPlugin
    {
        private const string ModName = "MountManagerExampleMod";
        private const string ModVersion = "1.0.0";
        internal const string Author = "Azumatt";
        private const string ModGUID = Author + "." + ModName;
        private static string ConfigFileName = ModGUID + ".cfg";
        private static string ConfigFileFullPath = Paths.ConfigPath + Path.DirectorySeparatorChar + ConfigFileName;
        
        private readonly Harmony _harmony = new(ModGUID);

        public static readonly ManualLogSource MountManagerLogger =
            BepInEx.Logging.Logger.CreateLogSource(ModName);

        private static readonly ConfigSync ConfigSync = new(ModGUID)
            { DisplayName = ModName, CurrentVersion = ModVersion, MinimumRequiredVersion = ModVersion };

        private void Awake()
        {
             ConfigSync.IsLocked = true; // Force locking the ServerSync configuration to the admin clients. Look at ServerSync documention for further information.
            
            /* Flying Mount */
            // Register the effect, item, and saddle for the mount using ItemManager.
            GameObject effectfly = ItemManager.PrefabManager.RegisterPrefab("azumatt_testing", "flyeffect1", "MyMounts");
            
            Item summonItem = new("azumatt_testing", "summonItem1", "MyMounts");
            summonItem.Name.English("Flying Mount Summon"); // You can use this to fix the display name in code
            summonItem.Description.English("Summon a Flying Mount");
            summonItem.Crafting.Add(CraftingTable.Workbench, 1); // Custom crafting stations can be specified as a string
            summonItem.RequiredItems.Add("Raspberry", 50);
            summonItem.RequiredItems.Add("Blueberries", 50);
            
            Item flyingSaddle = new("azumatt_testing", "testSaddle", "MyMounts");
            flyingSaddle.Name.English("Horse Saddle"); // You can use this to fix the display name in code
            flyingSaddle.Description.English("Horse Saddle!");
            flyingSaddle.Crafting.Add(CraftingTable.Workbench, 1); // Custom crafting stations can be specified as a string
            flyingSaddle.RequiredItems.Add("LeatherScraps", 10);
            flyingSaddle.RequiredItems.Add("BronzeNails", 5);
            
            // Register the mount using MountManager.
            Mount flyingMount = new(azumatttesting, "FlyingMount")
            {
                Animation = "", // The player animation to trigger when the mount is summoned. Not setting this value will use the default animation. gpower
                Type = MountType.Flying, // Sets the mount type for the mount, otherwise it will be determined based on Unity values found on the Humanoid and MonsterAI scripts.
                AdminOnly = false, // Admins should be the only ones that can summon this mount.
                UseInVoid = true, // Toggles the ability to use the mount in the void (vanilla "out of bounds" area), if you have EdgeOfWorldKill turned off, but don't want them using the mount in that area.
                Cooldown = 1f, // Time to wait (in seconds) between uses of the mount.
                UseInCombat = false, // Toggles whether the mount is usable while in combat.
                PassengerAttachmentPoint = "friendattach", // Specifies the passengers attachment point for the mount. (Currently testing!)
                AttachmentPoint = "attachpoint", // Specifies the attachment point for the mount. This is the place where the player should attach. If not set, it will use the default attachment point of attachpoint.
                SummonItem = summonItem.Prefab, // Item that will be used to summon the mount
                ExplosionEffect = effectfly // Visual explosion effect that will show when the mount is summoned (or whatever effect you want to use)
                // NoConfig = false, // Toggles whether the mount should be configurable in the config file or BepInExConfigurationManager. OPTIONAL!
            };
            
            
            /* Horse Mount */
            // Register the effect, item, and saddle for the mount using ItemManager.
            GameObject effectLand = ItemManager.PrefabManager.RegisterPrefab("horses", "horseEffect1");
            
            Item summonItem2 = new("horses", "summonItem2");
            summonItem2.Name.English("Horse Summon"); // You can use this to fix the display name in code
            summonItem2.Description.English("Summon a Horse!");
            summonItem2.Crafting.Add(CraftingTable.Workbench, 1); // Custom crafting stations can be specified as a string
            summonItem2.RequiredItems.Add("Raspberry", 50);
            summonItem2.RequiredItems.Add("Blueberries", 50);
            
            Item HorseSaddle = new("horses", "testHorseSaddle");
            HorseSaddle.Name.English("Horse Saddle"); // You can use this to fix the display name in code
            HorseSaddle.Description.English("Horse Saddle!");
            HorseSaddle.Crafting.Add(CraftingTable.Workbench, 1); // Custom crafting stations can be specified as a string
            HorseSaddle.RequiredItems.Add("LeatherScraps", 10);
            HorseSaddle.RequiredItems.Add("BronzeNails", 5);
            
            // Register the mount using MountManager.
            Mount horsey = new(azumatttesting, "BPHorse")
            {
                Animation = "wave", // The player animation to trigger when the mount is summoned. Not setting this value will use the default animation. gpower
                Type = MountType.Land, // Sets the mount type for the mount, otherwise it will be determined based on Unity values.
                AdminOnly = false, // Admins should be the only ones that can summon this mount.
                UseInVoid = true, // Toggles the ability to use the mount in the void (vanilla "out of bounds" area), if you have EdgeOfWorldKill turned off, but don't want them using the mount in that area.
                Cooldown = 10f, // Time to wait (in seconds) between uses of the mount.
                UseInCombat = false, // Toggles whether the mount is usable while in combat.
                PassengerAttachmentPoint = "friendattach", // Specifies the passengers attachment point for the mount. (Currently testing!)
                AttachmentPoint = "HorseyAttachPoint", // Specifies the attachment point for the mount. This is the place where the player should attach. If not set, it will use the default attachment point of attachpoint.
                SummonItem = summonItem2.Prefab, // Item that will be used to summon the mount
                ExplosionEffect = effectLand // Visual explosion effect that will show when the mount is summoned (or whatever effect you want to use)
            };
          
        }
    }
}
```