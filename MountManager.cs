using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using JetBrains.Annotations;
using UnityEngine;

namespace MountManager;

[PublicAPI]
public enum MountType
{
    Flying,
    Land,
    Water
}

public enum Toggle
{
    On,
    Off
}

[PublicAPI]
[Description("Registers a prefab with the mount manager. Allowing you to summon the mount")]
public class Mount
{
    internal class MountConfig
    {
        public ConfigEntry<MountType> Type = null!;
        public ConfigEntry<Toggle> AdminOnly = null!;
        public ConfigEntry<Toggle> SpawnOnly = null!;
        public ConfigEntry<Toggle> UseInVoid = null!;
        public ConfigEntry<Toggle> UseInCombat = null!;

        public ConfigEntry<float> Cooldown = null!;
        // Maybe add more configs later?
    }

    internal static readonly List<Mount> RegisteredMounts = new();
    internal static readonly List<GameObject> MountItems = new();
    internal static readonly List<GameObject> MountPrefabs = new();
    internal static Dictionary<Mount, MountConfig> MountConfigs = new();
    internal static readonly Dictionary<string, string> FriendAttachmentPointNames = new();
    internal static readonly Dictionary<string, string> AttachmentPointNames = new();
    internal static readonly Dictionary<string, GameObject?> AllRegisteredMountEffects = new();
    internal static readonly Dictionary<GameObject, float> MountCooldowns = new();
    internal static bool admin = false;
    internal static GameObject CurrentFab = null!;

    internal static List<string> AttachNamesList = new()
    {
        "landattach", "waterattach", "flyattach"
    };

    internal static Dictionary<GameObject, DateTime> LastGPowerUse = new();

    internal static Dictionary<string, DateTime> LastMountUse = new();
    //internal static DateTime LastGpower;
    //internal static DateTime LastMount;

    [Description(
        "Disables generation of the configs for your mounts. This is global, this turns it off for all mounts in your mod.")]
    public static bool ConfigurationEnabled = true;

    public readonly GameObject Prefab;

    [Description("Item that will be used to summon the mount")]
    public GameObject SummonItem = new();

    [Description("Visual explosion effect that will show when the mount is summoned")]
    public GameObject ExplosionEffect = new();

    [Description("Sets the mount type for the mount, otherwise it will be determined based on Unity values.")]
    public MountType Type;

    /*[Description(
        "Should the mount be able to be used via an item you have registered (use the ItemManager to register it!), or spawned in only.")]
    public bool SpawnOnly;*/

    [Description(
        "The player animation to trigger when the mount is summoned. Not setting this value will use the default animation. gpower")]
    public string Animation = "gpower";

    [Description(
        "Time to wait (in seconds) between uses of the mount.")]
    public float Cooldown;

    [Description(
        "Toggles whether the mount is usable while in combat.")]
    public bool UseInCombat;

    [Description(
        "Toggles the ability to use the mount in the void, if you have EdgeOfWorldKill turned off, but don't want them using the mount in that area.")]
    public bool UseInVoid;

    [Description("Admins should be the only ones that can summon this mount.")]
    public bool AdminOnly;

    [Description("Turns off generating a config for this mount.")]
    public bool NoConfig;

    [Description("Specifies the passengers attachment point for the mount.")]
    public string PassengerAttachmentPoint = "friendattach";

    [Description(
        "Specifies the attachment point for the mount. This is the place where the player should attach. If not set, it will use the default attachment point of attachpoint.")]
    public string AttachmentPoint = "attachpoint";

    private LocalizeKey? _name;

    public LocalizeKey Name
    {
        get
        {
            if (_name is { } name)
            {
                return name;
            }

            Humanoid data = Prefab.GetComponent<Humanoid>();
            if (data.m_name.StartsWith("$"))
            {
                _name = new LocalizeKey(data.m_name);
            }
            else
            {
                string key = "$mount_" + Prefab.name.Replace(" ", "_");
                _name = new LocalizeKey(key).English(data.m_name);
                data.m_name = key;
            }

            return _name;
        }
    }

    [Description("Registers a prefab with the mount manager. Allowing you to summon the mount")]
    public Mount(string assetBundleFileName, string prefabName, string folderName = "assets") : this(
        MountPrefabManager.RegisterAssetBundle(assetBundleFileName, folderName), prefabName)
    {
    }

    [Description("Registers a prefab with the mount manager. Allowing you to summon the mount")]
    public Mount(AssetBundle bundle, string prefabName)
    {
        Prefab = MountPrefabManager.RegisterPrefab(bundle, prefabName);
        RegisteredMounts.Add(this);
        FriendAttachmentPointNames.Add(Prefab.name, PassengerAttachmentPoint);
        AttachmentPointNames.Add(Prefab.name, AttachmentPoint);
        MountCooldowns.Add(Prefab, Cooldown);
        MountPrefabs.Add(Prefab);
        LastGPowerUse.Add(Prefab, DateTime.MinValue);
    }

    private class ConfigurationManagerAttributes
    {
        [UsedImplicitly] public int? Order;
        [UsedImplicitly] public bool? Browsable = true;
        [UsedImplicitly] public string? Category;
        [UsedImplicitly] public Action<ConfigEntryBase>? CustomDrawer = null!;
    }

    private static object? _configManager;

    internal static void Patch_FejdStartup()
    {
        Assembly? bepinexConfigManager = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "ConfigurationManager");

        Type? configManagerType = bepinexConfigManager?.GetType("ConfigurationManager.ConfigurationManager");
        _configManager = configManagerType == null
            ? null
            : BepInEx.Bootstrap.Chainloader.ManagerObject.GetComponent(configManagerType);

        /*void ReloadConfigDisplay() =>
            configManagerType?.GetMethod("BuildSettingList")!.Invoke(configManager, Array.Empty<object>());*/


        if (ConfigurationEnabled)
        {
            bool saveOnConfigSet = plugin.Config.SaveOnConfigSet;
            plugin.Config.SaveOnConfigSet = false;
            foreach (Mount mount in RegisteredMounts)
            {
                if (!MountItems.Contains(mount.SummonItem)) MountItems.Add(mount.SummonItem);
                if (mount.ExplosionEffect && !AllRegisteredMountEffects.ContainsKey(mount.Prefab.name))
                    AllRegisteredMountEffects.Add(mount.Prefab.name, mount.ExplosionEffect);
                if (mount.NoConfig) continue;
                MountConfig cfg = MountConfigs[mount] = new MountConfig();
                Humanoid mountPrefab = mount.Prefab.GetComponent<Humanoid>();
                string mountName = mountPrefab.m_name;
                string englishName = new Regex("['[\"\\]]").Replace(english.Localize(mountName), "").Trim();
                string? localizedName = Localization.instance.Localize(mountName).Trim();
                if (!LastMountUse.ContainsKey(mount.SummonItem.name))
                    LastMountUse.Add(mount.SummonItem.name,
                        DateTime
                            .MinValue); // Have to add this here, can't add when mount is created, you'll get a null GO for summon item

                int order = 0;

                cfg.Type = config(RemoveSpecialCharactersAndSpaces(localizedName), "Mount Type",
                    mount.Type,
                    new ConfigDescription($"Type or behaviour that {localizedName} should follow.", null,
                        new ConfigurationManagerAttributes
                            { Order = --order, Category = RemoveSpecialCharactersAndSpaces(localizedName) }));
                cfg.AdminOnly = config(RemoveSpecialCharactersAndSpaces(localizedName), "Admin Only",
                    mount.AdminOnly ? Toggle.On : Toggle.Off,
                    new ConfigDescription($"Should {localizedName} be an Admin only mount.", null,
                        new ConfigurationManagerAttributes
                            { Order = --order, Category = RemoveSpecialCharactersAndSpaces(localizedName) }));
                /*cfg.SpawnOnly = config(RemoveSpecialCharactersAndSpaces(localizedName), "Spawn Only",
                    mount.SpawnOnly,
                    new ConfigDescription($"Should {localizedName} be a mount that can only be spawned and is not linked to an item.", null,
                        new ConfigurationManagerAttributes
                            { Order = --order, Category = RemoveSpecialCharactersAndSpaces(localizedName) }));*/
                cfg.UseInVoid = config(RemoveSpecialCharactersAndSpaces(localizedName), "UseInVoid",
                    mount.UseInVoid ? Toggle.On : Toggle.Off,
                    new ConfigDescription(
                        $"Should {localizedName} be able to be used outside of the world radius. A.K.A. the void.",
                        null,
                        new ConfigurationManagerAttributes
                            { Order = --order, Category = RemoveSpecialCharactersAndSpaces(localizedName) }));
                cfg.UseInCombat = config(RemoveSpecialCharactersAndSpaces(localizedName), "UseInCombat",
                    mount.UseInCombat ? Toggle.On : Toggle.Off,
                    new ConfigDescription(
                        $"Should {localizedName} be able to be used if the player has just or is currently experiencing combat.",
                        null,
                        new ConfigurationManagerAttributes
                            { Order = --order, Category = RemoveSpecialCharactersAndSpaces(localizedName) }));
                cfg.Cooldown = config(RemoveSpecialCharactersAndSpaces(localizedName), "Mount Cooldown",
                    mount.Cooldown,
                    new ConfigDescription(
                        $"How long should you wait before being able to summon {localizedName} again. Value is in seconds ",
                        null,
                        new ConfigurationManagerAttributes
                            { Order = --order, Category = RemoveSpecialCharactersAndSpaces(localizedName) }));
                ConfigurationManagerAttributes customTableAttributes = new()
                {
                    Order = --order, Category = localizedName
                };

                void MountConfigChanged(object o, EventArgs e)
                {
                    foreach (Mount registeredMount in RegisteredMounts)
                    {
                        MountConfigs.TryGetValue(registeredMount, out MountConfig? confg);
                        var mountComponent = registeredMount.Prefab.GetComponent<MountComponent>();
                        if (confg != null)
                        {
                            mountComponent.mountType = registeredMount.Type = confg.Type.Value;
                            registeredMount.Cooldown = confg.Cooldown.Value;
                            MountCooldowns[registeredMount.Prefab] = confg.Cooldown.Value;
                            registeredMount.AdminOnly = confg.AdminOnly.Value != Toggle.Off;
                            //registeredMount.SpawnOnly = confg.SpawnOnly.Value;
                            registeredMount.UseInVoid = confg.UseInVoid.Value != Toggle.Off;
                            registeredMount.UseInCombat = confg.UseInCombat.Value != Toggle.Off;
                        }
                    }
                }

                cfg.Type.SettingChanged += MountConfigChanged;
                cfg.AdminOnly.SettingChanged += MountConfigChanged;
                cfg.Cooldown.SettingChanged += MountConfigChanged;
                cfg.UseInVoid.SettingChanged += MountConfigChanged;
                cfg.UseInCombat.SettingChanged += MountConfigChanged;
            }

            if (saveOnConfigSet)
            {
                plugin.Config.SaveOnConfigSet = true;
                plugin.Config.Save();
            }
        }
    }


    private static Localization? _english;

    private static Localization english
    {
        get
        {
            if (_english != null) return _english;
            _english = new Localization();
            _english.SetupLanguage("English");

            return _english;
        }
    }

    internal static string RegisterMountManagerMountSeat = "";
    internal static string RegisterMountManagerMountSeatRemove = "";

    // ReSharper disable once InconsistentNaming
    internal static BaseUnityPlugin? _plugin;

    // ReSharper disable once InconsistentNaming
    internal static BaseUnityPlugin plugin
    {
        get
        {
            if (_plugin is not null) return _plugin;
            IEnumerable<TypeInfo> types;
            try
            {
                types = Assembly.GetExecutingAssembly().DefinedTypes.ToList();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = e.Types.Where(t => t != null).Select(t => t.GetTypeInfo());
            }

            _plugin = (BaseUnityPlugin)BepInEx.Bootstrap.Chainloader.ManagerObject.GetComponent(types.First(t =>
                t.IsClass && typeof(BaseUnityPlugin).IsAssignableFrom(t)));
            RegisterMountManagerMountSeat = _plugin.name +
                                            $"Get{RemoveSpecialCharactersAndSpaces(_plugin.name)}MountManagerMountSeat";
            RegisterMountManagerMountSeatRemove = _plugin.name +
                                                  $"Remove{RemoveSpecialCharactersAndSpaces(_plugin.name)}MountManagerMountSeat";

            return _plugin;
        }
    }

    private static bool _hasConfigSync = true;
    private static object? _configSync;

    private static object? configSync
    {
        get
        {
            if (_configSync != null || !_hasConfigSync) return _configSync;
            if (Assembly.GetExecutingAssembly().GetType("ServerSync.ConfigSync") is { } configSyncType)
            {
                _configSync = Activator.CreateInstance(configSyncType, plugin.Info.Metadata.GUID + " MountManager");
                configSyncType.GetField("CurrentVersion")
                    .SetValue(_configSync, plugin.Info.Metadata.Version.ToString());
                configSyncType.GetProperty("IsLocked")!.SetValue(_configSync, true);
            }
            else
            {
                _hasConfigSync = false;
            }

            return _configSync;
        }
    }

    private static ConfigEntry<T> config<T>(string group, string name, T value, ConfigDescription description)
    {
        ConfigEntry<T> configEntry = plugin.Config.Bind(group, name, value, description);

        configSync?.GetType().GetMethod("AddConfigEntry")!.MakeGenericMethod(typeof(T))
            .Invoke(configSync, new object[] { configEntry });

        return configEntry;
    }

    private static ConfigEntry<T> config<T>(string group, string name, T value, string description) =>
        config(group, name, value, new ConfigDescription(description));

    public static string RemoveSpecialCharactersAndSpaces(string? str)
    {
        return Regex.Replace(str, "[^a-zA-Z0-9_.]+", "", RegexOptions.Compiled);
    }
}

[PublicAPI]
public class LocalizeKey
{
    private static readonly List<LocalizeKey> Keys = new();

    public readonly string Key;
    public readonly Dictionary<string, string> Localizations = new();

    public LocalizeKey(string key) => Key = key.Replace("$", "");

    public void Alias(string alias)
    {
        Localizations.Clear();
        if (!alias.Contains("$"))
        {
            alias = $"${alias}";
        }

        Localizations["alias"] = alias;
        Localization.instance.AddWord(Key, Localization.instance.Localize(alias));
    }

    public LocalizeKey English(string key) => AddForLang("English", key);
    public LocalizeKey Swedish(string key) => AddForLang("Swedish", key);
    public LocalizeKey French(string key) => AddForLang("French", key);
    public LocalizeKey Italian(string key) => AddForLang("Italian", key);
    public LocalizeKey German(string key) => AddForLang("German", key);
    public LocalizeKey Spanish(string key) => AddForLang("Spanish", key);
    public LocalizeKey Russian(string key) => AddForLang("Russian", key);
    public LocalizeKey Romanian(string key) => AddForLang("Romanian", key);
    public LocalizeKey Bulgarian(string key) => AddForLang("Bulgarian", key);
    public LocalizeKey Macedonian(string key) => AddForLang("Macedonian", key);
    public LocalizeKey Finnish(string key) => AddForLang("Finnish", key);
    public LocalizeKey Danish(string key) => AddForLang("Danish", key);
    public LocalizeKey Norwegian(string key) => AddForLang("Norwegian", key);
    public LocalizeKey Icelandic(string key) => AddForLang("Icelandic", key);
    public LocalizeKey Turkish(string key) => AddForLang("Turkish", key);
    public LocalizeKey Lithuanian(string key) => AddForLang("Lithuanian", key);
    public LocalizeKey Czech(string key) => AddForLang("Czech", key);
    public LocalizeKey Hungarian(string key) => AddForLang("Hungarian", key);
    public LocalizeKey Slovak(string key) => AddForLang("Slovak", key);
    public LocalizeKey Polish(string key) => AddForLang("Polish", key);
    public LocalizeKey Dutch(string key) => AddForLang("Dutch", key);
    public LocalizeKey Portuguese_European(string key) => AddForLang("Portuguese_European", key);
    public LocalizeKey Portuguese_Brazilian(string key) => AddForLang("Portuguese_Brazilian", key);
    public LocalizeKey Chinese(string key) => AddForLang("Chinese", key);
    public LocalizeKey Japanese(string key) => AddForLang("Japanese", key);
    public LocalizeKey Korean(string key) => AddForLang("Korean", key);
    public LocalizeKey Hindi(string key) => AddForLang("Hindi", key);
    public LocalizeKey Thai(string key) => AddForLang("Thai", key);
    public LocalizeKey Abenaki(string key) => AddForLang("Abenaki", key);
    public LocalizeKey Croatian(string key) => AddForLang("Croatian", key);
    public LocalizeKey Georgian(string key) => AddForLang("Georgian", key);
    public LocalizeKey Greek(string key) => AddForLang("Greek", key);
    public LocalizeKey Serbian(string key) => AddForLang("Serbian", key);
    public LocalizeKey Ukrainian(string key) => AddForLang("Ukrainian", key);

    private LocalizeKey AddForLang(string lang, string value)
    {
        Localizations[lang] = value;
        if (Localization.instance.GetSelectedLanguage() == lang)
        {
            Localization.instance.AddWord(Key, value);
        }
        else if (lang == "English" && !Localization.instance.m_translations.ContainsKey(Key))
        {
            Localization.instance.AddWord(Key, value);
        }

        return this;
    }

    [HarmonyPriority(Priority.LowerThanNormal)]
    internal static void AddLocalizedKeys(Localization __instance, string language)
    {
        foreach (LocalizeKey key in Keys)
        {
            if (key.Localizations.TryGetValue(language, out string Translation) ||
                key.Localizations.TryGetValue("English", out Translation))
            {
                __instance.AddWord(key.Key, Translation);
            }
            else if (key.Localizations.TryGetValue("alias", out string alias))
            {
                Localization.instance.AddWord(key.Key, Localization.instance.Localize(alias));
            }
        }
    }
}

public class AdminSyncing
{
    private static bool _isServer;

    [HarmonyPriority(Priority.VeryHigh)]
    internal static void AdminStatusSync(ZNet __instance)
    {
        _isServer = __instance.IsServer();
        ZRoutedRpc.instance.Register<ZPackage>(
            Mount.RemoveSpecialCharactersAndSpaces(Mount._plugin?.Info.Metadata.Name) + " MMAdminStatusSync",
            RPC_AdminPieceAddRemove);

        IEnumerator WatchAdminListChanges()
        {
            List<string> CurrentList = new(ZNet.instance.m_adminList.GetList());
            for (;;)
            {
                yield return new WaitForSeconds(30);
                if (!ZNet.instance.m_adminList.GetList().SequenceEqual(CurrentList))
                {
                    CurrentList = new List<string>(ZNet.instance.m_adminList.GetList());
                    List<ZNetPeer> adminPeer = ZNet.instance.GetPeers().Where(p =>
                        ZNet.instance.m_adminList.Contains(p.m_rpc.GetSocket().GetHostName())).ToList();
                    List<ZNetPeer> nonAdminPeer = ZNet.instance.GetPeers().Except(adminPeer).ToList();
                    SendAdmin(nonAdminPeer, false);
                    SendAdmin(adminPeer, true);

                    void SendAdmin(List<ZNetPeer> peers, bool isAdmin)
                    {
                        ZPackage package = new();
                        package.Write(isAdmin);
                        ZNet.instance.StartCoroutine(SendZPackage(peers, package));
                    }
                }
            }
            // ReSharper disable once IteratorNeverReturns
        }

        if (_isServer)
        {
            ZNet.instance.StartCoroutine(WatchAdminListChanges());
        }
    }

    private static IEnumerator SendZPackage(List<ZNetPeer> peers, ZPackage package)
    {
        if (!ZNet.instance)
        {
            yield break;
        }

        const int compressMinSize = 10000;

        if (package.GetArray() is { LongLength: > compressMinSize } rawData)
        {
            ZPackage compressedPackage = new();
            compressedPackage.Write(4);
            MemoryStream output = new();
            using (DeflateStream deflateStream = new(output, System.IO.Compression.CompressionLevel.Optimal))
            {
                deflateStream.Write(rawData, 0, rawData.Length);
            }

            compressedPackage.Write(output.ToArray());
            package = compressedPackage;
        }

        List<IEnumerator<bool>> writers =
            peers.Where(peer => peer.IsReady()).Select(p => TellPeerAdminStatus(p, package)).ToList();
        writers.RemoveAll(writer => !writer.MoveNext());
        while (writers.Count > 0)
        {
            yield return null;
            writers.RemoveAll(writer => !writer.MoveNext());
        }
    }

    private static IEnumerator<bool> TellPeerAdminStatus(ZNetPeer peer, ZPackage package)
    {
        if (ZRoutedRpc.instance is not { } rpc)
        {
            yield break;
        }

        SendPackage(package);

        void SendPackage(ZPackage pkg)
        {
            string method = Mount.RemoveSpecialCharactersAndSpaces(Mount._plugin?.Info.Metadata.Name) +
                            " MMAdminStatusSync";
            if (_isServer)
            {
                peer.m_rpc.Invoke(method, pkg);
            }
            else
            {
                rpc.InvokeRoutedRPC(peer.m_server ? 0 : peer.m_uid, method, pkg);
            }
        }
    }

    internal static void RPC_AdminPieceAddRemove(long sender, ZPackage package)
    {
        ZNetPeer? currentPeer = ZNet.instance.GetPeer(sender);
        try
        {
            Mount.admin = package.ReadBool();
        }
        catch
        {
            // ignore
        }

        if (_isServer)
        {
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody,
                Mount.RemoveSpecialCharactersAndSpaces(Mount._plugin?.Info.Metadata.Name) + " MMAdminStatusSync",
                new ZPackage());
            if (ZNet.instance.m_adminList.Contains(currentPeer.m_rpc.GetSocket().GetHostName()))
            {
                ZPackage pkg = new();
                pkg.Write(true);
                currentPeer.m_rpc.Invoke(
                    Mount.RemoveSpecialCharactersAndSpaces(Mount._plugin?.Info.Metadata.Name) + " MMAdminStatusSync",
                    pkg);
            }
        }
    }

    internal static void RPC_InitialAdminSync(ZRpc rpc, ZPackage package) =>
        RPC_AdminPieceAddRemove(0, package);
}

public static class MountPrefabManager
{
    static MountPrefabManager()
    {
        Harmony harmony = new("org.bepinex.helpers.MountManager");
        harmony.Patch(AccessTools.DeclaredMethod(typeof(FejdStartup), nameof(FejdStartup.Awake)),
            new HarmonyMethod(AccessTools.DeclaredMethod(typeof(Mount),
                nameof(Mount.Patch_FejdStartup))));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(ZNetScene), nameof(ZNetScene.Awake)),
            new HarmonyMethod(AccessTools.DeclaredMethod(typeof(MountPrefabManager),
                nameof(Patch_ZNetSceneAwake))));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(ZNet), nameof(ZNet.OnNewConnection)),
            new HarmonyMethod(AccessTools.DeclaredMethod(typeof(MountPrefabManager),
                nameof(Patch_ZNetNewConnection))));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(ZNet), nameof(ZNet.Awake)),
            postfix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(AdminSyncing),
                nameof(AdminSyncing.AdminStatusSync))));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(Localization), nameof(Localization.LoadCSV)),
            postfix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(LocalizeKey),
                nameof(LocalizeKey.AddLocalizedKeys))));
        /* Mount Component Specific */
        harmony.Patch(AccessTools.DeclaredMethod(typeof(Humanoid), nameof(Humanoid.IsTeleportable)),
            postfix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(MountPrefabManager),
                nameof(Patch_isTeleportable))));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(Player), nameof(Player.ConsumeItem)),
            new HarmonyMethod(AccessTools.DeclaredMethod(typeof(MountPrefabManager),
                nameof(Patch_PlayerConsumeItem))));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(Player), nameof(Player.ActivateGuardianPower)),
            new HarmonyMethod(AccessTools.DeclaredMethod(typeof(MountPrefabManager),
                nameof(Patch_PlayerActivateGuardianPower))));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(Player), nameof(Player.UseHotbarItem)),
            new HarmonyMethod(AccessTools.DeclaredMethod(typeof(MountPrefabManager),
                nameof(Patch_PlayerUseHotbarItem))));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(Player), nameof(Player.AttachStop)),
            new HarmonyMethod(AccessTools.DeclaredMethod(typeof(MountPrefabManager),
                nameof(Patch_PlayerAttachStop))));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(InventoryGui), nameof(InventoryGui.OnRightClickItem)),
            new HarmonyMethod(AccessTools.DeclaredMethod(typeof(MountPrefabManager),
                nameof(Patch_InventoryGuiOnRightClickItem))));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(CharacterAnimEvent), nameof(CharacterAnimEvent.GPower)),
            new HarmonyMethod(AccessTools.DeclaredMethod(typeof(MountPrefabManager),
                nameof(Patch_CharacterAnimEventGPower))));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(ZSyncAnimation), nameof(ZSyncAnimation.SetTrigger)),
            new HarmonyMethod(AccessTools.DeclaredMethod(typeof(MountPrefabManager),
                nameof(Patch_ZSyncAnimationSetTrigger))));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(Player), nameof(Player.Update)),
            postfix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(MountPrefabManager),
                nameof(Patch_Update_Player))));
    }

    private struct BundleId
    {
        [UsedImplicitly] public string assetBundleFileName;
        [UsedImplicitly] public string folderName;
    }

    private static readonly Dictionary<BundleId, AssetBundle> bundleCache = new();

    public static AssetBundle RegisterAssetBundle(string assetBundleFileName, string folderName = "assets")
    {
        BundleId id = new() { assetBundleFileName = assetBundleFileName, folderName = folderName };
        if (!bundleCache.TryGetValue(id, out AssetBundle assets))
        {
            assets = bundleCache[id] =
                Resources.FindObjectsOfTypeAll<AssetBundle>().FirstOrDefault(a => a.name == assetBundleFileName) ??
                AssetBundle.LoadFromStream(Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream(Assembly.GetExecutingAssembly().GetName().Name + $".{folderName}." +
                                               assetBundleFileName));
        }

        return assets;
    }

    private static readonly List<GameObject> mountPrefabs = new();
    private static readonly List<GameObject> ZnetOnlyPrefabs = new();

    public static GameObject RegisterPrefab(string assetBundleFileName, string prefabName,
        string folderName = "assets") =>
        RegisterPrefab(RegisterAssetBundle(assetBundleFileName, folderName), prefabName);

    public static GameObject RegisterPrefab(AssetBundle assets, string prefabName, bool isEffect = false)
    {
        GameObject prefab = assets.LoadAsset<GameObject>(prefabName);
        if (prefab == null)
        {
            Debug.LogError(
                $"Could not find prefab {prefabName} in {assets.name} for mod {Mount._plugin?.Info.Metadata.Name}");
            return null!;
        }

        if (isEffect)
        {
            ZnetOnlyPrefabs.Add(prefab);
        }
        else
        {
            prefab.AddComponent<MountComponent>();
            mountPrefabs.Add(prefab);
        }

        return prefab;
    }

    /* Sprites Only! */
    public static Sprite RegisterSprite(string assetBundleFileName, string prefabName,
        string folderName = "assets") =>
        RegisterSprite(RegisterAssetBundle(assetBundleFileName, folderName), prefabName);

    public static Sprite RegisterSprite(AssetBundle assets, string prefabName)
    {
        Sprite prefab = assets.LoadAsset<Sprite>(prefabName);
        return prefab;
    }

    [HarmonyPriority(Priority.VeryHigh)]
    private static void Patch_ZNetNewConnection(ZNet __instance, ZNetPeer peer)
    {
        if (!__instance.IsServer())
        {
            peer.m_rpc.Register<ZPackage>(
                Mount.RemoveSpecialCharactersAndSpaces(Mount._plugin?.Info.Metadata.Name) + " MMAdminStatusSync",
                AdminSyncing.RPC_InitialAdminSync);
        }
        else
        {
            ZPackage packge = new();
            packge.Write(__instance.m_adminList.Contains(peer.m_rpc.GetSocket().GetHostName()));

            peer.m_rpc.Invoke(
                Mount.RemoveSpecialCharactersAndSpaces(Mount._plugin?.Info.Metadata.Name) + " MMAdminStatusSync",
                packge);
        }
    }


    [HarmonyPriority(Priority.VeryHigh)]
    private static void Patch_ZNetSceneAwake(ZNetScene __instance)
    {
        foreach (GameObject prefab in mountPrefabs.Concat(ZnetOnlyPrefabs))
        {
            if (!__instance.m_prefabs.Contains(prefab))
                __instance.m_prefabs.Add(prefab);
        }
    }

    /* Mount Component Specific */
    [HarmonyPriority(Priority.Normal)]
    private static void Patch_isTeleportable(ref bool __result)
    {
        __result = __result && !MountComponent.IsRidingBool();
    }

    [HarmonyPriority(Priority.Normal)]
    private static bool Patch_PlayerConsumeItem()
    {
        if (!MountComponent.IsRidingBool()) return true;
        Player.m_localPlayer.Message(MessageHud.MessageType.Center,
            "Cannot consume items while riding a mount!");
        return false;
    }

    [HarmonyPriority(Priority.Normal)]
    private static bool Patch_PlayerActivateGuardianPower()
    {
        if (!MountComponent.IsRidingBool()) return true;
        Player.m_localPlayer.Message(MessageHud.MessageType.Center,
            "Cannot active your powers while riding a mount!");
        return false;
    }

    [HarmonyPriority(Priority.Normal)]
    private static bool Patch_InventoryGuiOnRightClickItem(InventoryGrid grid, ItemDrop.ItemData item)
    {
        if (grid.m_inventory != Player.m_localPlayer.m_inventory) return true;
        return MountComponent.CheckLastUsedItem(item);
    }

    [HarmonyPriority(Priority.Normal)]
    private static bool Patch_PlayerUseHotbarItem(int index)
    {
        ItemDrop.ItemData item = Player.m_localPlayer.m_inventory.GetItemAt(index - 1, 0);
        return MountComponent.CheckLastUsedItem(item);
    }

    [HarmonyPriority(Priority.Normal)]
    private static bool Patch_PlayerAttachStop(Player __instance)
    {
        if (__instance.m_attachPoint == null)
        {
            return true;
        }

        if (__instance.m_attached && MountComponent.IsRidingBool())
        {
            return false;
        }

        if (__instance.m_attached && Mount.FriendAttachmentPointNames.ContainsValue(__instance.m_attachPoint.name))
        {
            MountComponent comp =
                __instance.m_attachPoint.GetComponentInParent<MountComponent>();
            if (comp)
            {
                comp.znv?.InvokeRPC(Mount.RegisterMountManagerMountSeatRemove, new object?[] { null });
            }
        }

        return true;
    }

    [HarmonyPriority(Priority.Normal)]
    private static bool Patch_CharacterAnimEventGPower()
    {
        return (DateTime.Now - Mount.LastGPowerUse[Mount.CurrentFab]).TotalSeconds > 1;
    }

    [HarmonyPriority(Priority.Normal)]
    private static bool Patch_ZSyncAnimationSetTrigger()
    {
        return !MountComponent.IsRidingBool();
    }

    [HarmonyPriority(Priority.VeryHigh)]
    private static void Patch_Update_Player(Player __instance)
    {
        if (__instance.m_nview.GetZDO() != null && __instance.m_nview.IsOwner()) __instance.UpdateAttach();
    }
}

public class MountComponent : MonoBehaviour, Interactable, Hoverable
{
    private Transform _sitAttach = null!;
    private Transform _sitAttachFriend = null!;
    private Animator _animator = null!;
    private Rigidbody _rbody = null!;
    public ZNetView znv = null!;
    public Humanoid humanoid = null!;
    public MonsterAI monsterAI = null!;
    public Tameable tameable = null!;
    public Sadle saddle = null!;
    public MountType mountType;

    public bool Interact(Humanoid user, bool hold, bool alt)
    {
        if (IsSitAvaliable())
        {
            znv.InvokeRPC(Mount.RegisterMountManagerMountSeat, new object?[] { null });
            Player.m_localPlayer.AttachStart(_sitAttachFriend, gameObject, true, false, false, "attach_lox",
                Vector3.up / 80f);
            return true;
        }

        return false;
    }

    public bool UseItem(Humanoid user, ItemDrop.ItemData item)
    {
        return false;
    }

    public string GetHoverText()
    {
        return IsSitAvaliable()
            ? Localization.instance.Localize("[<color=yellow><b>$KEY_Use</b></color>] Ride")
            : "";
    }

    public string GetHoverName()
    {
        return "";
    }

    public void Awake()
    {
        string prefabNameClean = gameObject.name.Replace("(Clone)", "");
        _animator = gameObject.GetComponentInChildren<Animator>();
        znv = GetComponent<ZNetView>();
        _rbody = GetComponent<Rigidbody>();
        humanoid = GetComponent<Humanoid>();
        monsterAI = GetComponent<MonsterAI>();
        tameable = GetComponentInChildren<Tameable>();
        saddle = Utils.FindChild(transform, "saddle")
            .GetComponent<Sadle>(); // GetComponentInChildren<Sadle>(); was not working
        tameable.SetSaddle(true);
        znv.InvokeRPC("AddSaddle");
        gameObject.transform.localScale = Vector3.zero;
        znv.Register(Mount.RegisterMountManagerMountSeat, AttachOtherPlayer);
        znv.Register(Mount.RegisterMountManagerMountSeatRemove, DeAttachOtherPlayer);

        MountType typeValue = MountType.Flying;
        foreach (Mount registeredMount in Mount.RegisteredMounts)
        {
            if (registeredMount.Prefab.name != prefabNameClean) continue;
            if (registeredMount.NoConfig) continue;
            Mount.MountConfigs.TryGetValue(registeredMount, out Mount.MountConfig? confg);
            if (confg != null)
            {
                typeValue = confg.Type.Value;
            }
        }

        mountType =
            typeValue
                switch // This switch expression looks a bit redundant and stupid, but it makes the check below make sense if the config updates this value.
                {
                    MountType.Water => MountType.Water,
                    MountType.Flying => MountType.Flying,
                    MountType.Land => MountType.Land,
                    _ => MountType.Flying
                };

        if ((humanoid.m_canSwim && monsterAI.m_avoidLand) || mountType == MountType.Water)
        {
            mountType = MountType.Water;
            SetupMount(prefabNameClean, "waterattach");
        }
        else if (humanoid.m_flying || mountType == MountType.Flying)
        {
            mountType = MountType.Flying;
            SetupMount(prefabNameClean, "flyattach");
        }
        else if (monsterAI.m_avoidWater || mountType == MountType.Land)
        {
            mountType = MountType.Land;
            SetupMount(prefabNameClean, "landattach");
        }
        else
        {
            mountType = MountType.Flying;
            SetupMount(prefabNameClean, "flyattach");
        }
    }

    public void Start()
    {
        _animator.Update(0);
        StartCoroutine(ScaleX1());
    }

    private IEnumerator ScaleX1()
    {
        float count = 0;
        while (count <= 1f)
        {
            count += Time.deltaTime * 2f;
            gameObject.transform.localScale = new Vector3(count, count, count);
            yield return null;
        }

        gameObject.transform.localScale = Vector3.one;
    }

    private static IEnumerator StartMountCoroute(GameObject prefab, ItemDrop.ItemData item)
    {
        string prefabNameClean = prefab.name.Replace("(Clone)", "");
        
        bool voidEnabled = false;
        string animationtoPlay = "";
        bool adminOnly = true;
        bool combatUse = true;
        GameObject effect = null!;
        foreach (Mount registeredMount in Mount.RegisteredMounts)
        {
            if (registeredMount.Prefab == prefab)
            {
                Mount.CurrentFab = prefab;
                if (registeredMount.NoConfig) continue;
                Mount.MountConfig cfg = Mount.MountConfigs[registeredMount];
                voidEnabled = cfg.UseInVoid.Value == Toggle.On;
                animationtoPlay = string.IsNullOrWhiteSpace(registeredMount.Animation)
                    ? "gpower"
                    : registeredMount.Animation;
                adminOnly = cfg.AdminOnly.Value == Toggle.On;
                combatUse = cfg.UseInCombat.Value == Toggle.On;
            }
        }

        Player? p = Player.m_localPlayer;
        if (adminOnly && !Mount.admin)
        {
            p.Message(MessageHud.MessageType.Center, "This mount can only be used by an admin");
            yield break;
        }

        if (p.transform.position.y < 0 && !voidEnabled)
        {
            p.Message(MessageHud.MessageType.Center, "Cannot ride this mount in the void!");
            yield break;
        }

        List<Character> characters = new();
        Character.GetCharactersInRange(p.transform.position, 30f, characters);
        bool canuse = true;
        foreach (Character? character in characters
                     .Where(character => character != null && character.GetComponent<MonsterAI>()).Where(character =>
                         character.GetComponent<MonsterAI>().IsAlerted() && !combatUse))
        {
            canuse = false;
            p.Message(MessageHud.MessageType.Center, "Cannot ride this mount while in combat!");
        }

        if (!voidEnabled || !canuse || adminOnly) yield break;
        p.AttachStop();
        float count = 0;
        Mount.LastGPowerUse[prefab] = DateTime.Now;
        p.m_zanim.SetTrigger(animationtoPlay);
        while (count <= 1f)
        {
            if (Player.m_localPlayer.IsDead()) yield break;
            count += Time.deltaTime;
            Transform transform = Player.m_localPlayer.transform;
            Vector3 position = transform.position;
            position += Vector3.up * (Time.deltaTime * 6f);
            position += transform.forward * (Time.deltaTime * 6f);
            transform.position = position;
            p.m_body.angularVelocity = Vector3.zero;
            p.m_body.velocity = Vector3.zero;
            yield return null;
        }

        if (!IsRidingBool() && p.transform.position.y >= 0)
        {
            GameObject go = Instantiate(prefab, p.transform.position - Vector3.up * 3f,
                Quaternion.LookRotation(GameCamera.instance.transform.forward));
            GameObject? explosion = Mount.AllRegisteredMountEffects.ContainsKey(Utils.GetPrefabName(prefab))
                ? Mount.AllRegisteredMountEffects[Utils.GetPrefabName(prefab)]
                : null;
            go.GetComponent<MountComponent>().Setup(explosion, item);
        }
    }

    public void Setup(GameObject? explosion, ItemDrop.ItemData item)
    {
        StartCoroutine(MainCoroute(explosion, item));
    }

    private IEnumerator MainCoroute(GameObject? explosion, ItemDrop.ItemData item)
    {
        bool breakRide = false;
        Player? p = Player.m_localPlayer;
        if (p.IsDead())
        {
            if (gameObject) ZNetScene.instance.Destroy(gameObject);
            yield break;
        }

        if (explosion) Instantiate(explosion, transform.position, Quaternion.identity);
        p.m_body.useGravity = false;

        p.m_body.angularVelocity = Vector3.zero;
        p.m_body.velocity = Vector3.zero;
        p.AttachStart(_sitAttach, gameObject, true, false, false, "attach_lox", Vector3.zero);
        float prevCameraDistanceMax = GameCamera.instance.m_maxDistance;
        GameCamera.instance.m_maxDistance = 14f;
        for (;;)
        {
            p.UseStamina(Time.fixedDeltaTime *
                         2); // This decreases stamina by 1 every second.   If you want to change this to expend more stamina, change the value of p.UseStamina(Time.fixedDeltaTime) to something else like this p.UseStamina(Time.fixedDeltaTime * 20).
            GameCamera.instance.m_distance = 14f;
            if (p && (Input.GetKeyDown(KeyCode.Escape) || p.IsDead()) &&
                Player.m_localPlayer.m_attachPoint == _sitAttach)
            {
                Menu.instance.OnClose();
                breakRide = true;
            }

            if (p.IsDead() || breakRide || item == null || !p.m_inventory.ContainsItem(item) ||
                p.m_stamina == 0f || p.IsEncumbered())
            {
                BreakMount(p, prevCameraDistanceMax, explosion, item.m_dropPrefab.name);
                yield break;
            }

            if (mountType == MountType.Flying)
            {
                p.m_body.angularVelocity = Vector3.zero;
                p.m_body.velocity = Vector3.zero;
                Vector3 pos = transform.position;
                Vector3 fwd = GameCamera.instance.transform.forward;
                if (!Input.GetKey(KeyCode.Mouse1))
                {
                    Vector3 zero = Vector3.zero;
                    transform.rotation = Quaternion.LookRotation(fwd);
                    float mod = ZInput.GetButton("Run") || ZInput.GetButton("JoyRun") ? 16f : 10f;

                    if (ZInput.GetButton("Forward") || ZInput.GetJoyLeftStickY() < 0)
                    {
                        pos += fwd * (mod * Time.deltaTime);
                    }

                    if (ZInput.GetButton("Backward") || ZInput.GetJoyLeftStickY() > 0)
                    {
                        pos -= fwd * (mod * Time.deltaTime);
                    }


                    if (ZInput.GetButton("Crouch"))
                    {
                        transform.rotation = Quaternion.LookRotation(-transform.up + transform.forward * 2f);
                        pos.y -= mod * Time.deltaTime;
                    }

                    if (ZInput.GetButton("Jump"))
                    {
                        transform.rotation = Quaternion.LookRotation(transform.up + transform.forward * 2f);
                        pos.y += mod * Time.deltaTime;
                    }


                    pos.y = pos.y < ZoneSystem.instance.GetGroundHeight(pos) + 1
                        ? ZoneSystem.instance.GetGroundHeight(pos) + 1
                        : pos.y;
                }


                if (pos.y < 31) pos.y = 31;
                transform.position = pos;
                _rbody.velocity = Vector3.zero;
                _rbody.angularVelocity = Vector3.zero;
                p.m_maxAirAltitude = 0f;
                p.m_lastGroundTouch = 0f;
                p.UpdateAttach();
            }
            else
            {
                switch (mountType)
                {
                    case MountType.Water:
                        if (!monsterAI.m_character.IsSwiming())
                        {
                            monsterAI.m_aiStatus = "Move to water";
                            monsterAI.MoveToWater(Time.deltaTime, 100f);
                        }

                        break;
                    case MountType.Land:
                    case MountType.Flying:
                        if (!humanoid.m_canSwim && (monsterAI.m_character.IsSwiming() ||
                                                    transform.position.y <
                                                    ZoneSystem.instance.GetGroundHeight(transform.position) -
                                                    5))
                        {
                            breakRide = true;
                        }

                        break;
                }
            }


            yield return null;
        }
    }

    public static bool IsRidingBool()
    {
        if (Player.m_localPlayer && Player.m_localPlayer.m_attachPoint != null &&
            Mount.AttachNamesList.Contains(Player.m_localPlayer.m_attachPoint.name))
        {
            return true;
        }

        return false;
    }

    public void SetupMount(string prefabNameClean, string mountAttach)
    {
        _sitAttach = Utils.FindChild(transform, $"{mountAttach}");
        if (saddle)
        {
            saddle.m_attachPoint = _sitAttach;
            CustomMakeTame(tameable);
            saddle.Interact(Player.m_localPlayer, false, false);
            saddle.gameObject.SetActive(true);
        }

        Player.m_localPlayer.m_attachPoint = _sitAttach;
    }

    private void AttachOtherPlayer(long sender)
    {
        znv.m_zdo.Set("MountManagerMountOtherPlayerAttached", true);
    }

    private void BreakMount(Player p, float prevCameraDistanceMax, GameObject? explosion, string prefabName)
    {
        GameCamera.instance.m_maxDistance = prevCameraDistanceMax;
        p.m_body.velocity = Vector3.zero;
        p.m_body.useGravity = true;
        p.m_lastGroundTouch = 0f;
        p.m_maxAirAltitude = 0f;
        Mount.LastMountUse[prefabName] = DateTime.Now;
        if (explosion) Instantiate(explosion, transform.position, Quaternion.identity);
        if (gameObject) ZNetScene.instance.Destroy(gameObject);
    }

    private void DeAttachOtherPlayer(long sender)
    {
        znv.m_zdo.Set("MountManagerMountOtherPlayerAttached", false);
    }

    private bool IsSitAvaliable() => _sitAttachFriend != null && znv != null && znv.IsValid() &&
                                     !znv.IsOwner() &&
                                     !znv.m_zdo.GetBool("MountManagerMountOtherPlayerAttached");

    internal static bool CheckLastUsedItem(ItemDrop.ItemData item)
    {
        if (item != null && Player.m_localPlayer)
        {
            for (int i = 0; i < Mount.MountItems.Count; ++i)
            {
                var itemAt = Mount.MountItems[i];
                if (item.m_dropPrefab.name == itemAt.name)
                {
                    if ((DateTime.Now - Mount.LastMountUse[itemAt.name]).TotalSeconds >=
                        Mount.MountCooldowns[Mount.MountPrefabs[i]] &&
                        !IsRidingBool())
                    {
                        Mount.LastMountUse[itemAt.name] = DateTime.Now;
                        Player.m_localPlayer.StartCoroutine(StartMountCoroute(Mount.MountPrefabs[i], item));
                    }
                    else
                    {
                        if ((int)(Mount.MountCooldowns[Mount.MountPrefabs[i]] -
                                  (DateTime.Now - Mount.LastMountUse[itemAt.name]).TotalSeconds) >= 0)
                        {
                            Player.m_localPlayer.Message(MessageHud.MessageType.Center,
                                $"On cooldown for {(int)(Mount.MountCooldowns[Mount.MountPrefabs[i]] - (DateTime.Now - Mount.LastMountUse[itemAt.name]).TotalSeconds)} seconds");
                        }
                    }

                    return false;
                }
            }
        }

        return true;
    }

    private static void CustomMakeTame(Tameable tameable)
    {
        if (!tameable.m_nview.IsValid() || !tameable.m_nview.IsOwner() || tameable.m_character.IsTamed())
            return;
        tameable.m_monsterAI.MakeTame();
    }
}