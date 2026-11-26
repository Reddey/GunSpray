using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

// gunspray clientsided graffiti menu improver built against pan4urs mp mod 0.5.3, date Nov 24th 2026. 
[BepInPlugin(PluginGuid, "GunSpray", "1.0.0")]
[BepInDependency("com.gunsaw.multiplayer", BepInDependency.DependencyFlags.HardDependency)]
public sealed class GunSprayPlugin : BaseUnityPlugin
{
    public const string PluginGuid = "com.gunsaw.graffitifolders";
    private const int MaxIndexedImages = 512;
    private const int MaxSourceBytes = 1_048_576; // retain 1mb limit
    private const int MaxImageDimension = 1280;    // retain 1280px limit across either horizontal or vertical
    private const int Columns = 7;
    private const int ThumbnailSize = 88;
    private const int ThumbnailCacheLimit = 72;
    private const int ThumbnailBuildsPerFrame = 2;
    private const float CellStep = 118f;

    private static GunSprayPlugin active;
    private Harmony harmony;
    private Type systemType;
    private Type libraryImageType;
    private MethodInfo encodeTexture;
    private MethodInfo checkSourceDimensions;
    private MethodInfo cancelMethod;
    // gunfire guard, uses reflection. no assembly c# dependencies
    private Type localBodyType;
    private FieldInfo weaponBodyField;
    private FieldInfo localPlayerField;
    private PropertyInfo localPlayerProperty;
    private FieldInfo playerBodyField;
    private PropertyInfo playerBodyProperty;
    private bool shotGuardWarningLogged;
    private FieldInfo levitatorBodyField;
    private PropertyInfo levitatorBodyProperty;
    private FieldInfo levitatorHeldObjectField;
    private MethodInfo levitatorUnGrabMethod;
    private bool telekinesisGuardWarningLogged;
    private MethodInfo destroyLibraryImage;
    private FieldInfo libraryField;
    private FieldInfo pickerContentField;
    private FieldInfo pickerRootField;
    private FieldInfo pickerScrollField;
    private FieldInfo pickerDirtyField;
    private FieldInfo selectedPathField;
    private FieldInfo folderField;
    private FieldInfo watcherField;
    private FieldInfo placingField;
    private FieldInfo pickerOpenField;
    private FieldInfo timestampField;
    private FieldInfo pickerItemsField;
    private object system;
    private string rootFolder;
    private string currentFolder;
    private readonly Dictionary<string, IndexedFile> indexed = new Dictionary<string, IndexedFile>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Thumb> thumbnailCache = new Dictionary<string, Thumb>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> failedThumbnails = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private readonly List<Tile> imageTiles = new List<Tile>();
    private readonly Dictionary<string, Outline> selectionOutlines = new Dictionary<string, Outline>(StringComparer.OrdinalIgnoreCase);
    private GameObject rootUi;
    private GameObject contentUi;
    private ScrollRect scroll;
    private TMP_Text pathLabel;
    private TMP_Text countLabel;
    private Image backImage;
    private TMP_FontAsset uiFont;
    private Material uiFontMaterial;
    private bool navigationInitialized;
    private bool suppressEmptySelectionOnRelease;
    private int indexingErrorCount;
    private readonly List<string> scanProblems = new List<string>();

    private sealed class IndexedFile
    {
        internal string Path;
        internal DateTime Modified;
        internal long Length;
    }

    private sealed class Thumb
    {
        internal Texture2D Texture;
        internal DateTime Modified;
        internal int LastUsed;
    }

    private sealed class Tile
    {
        internal string Path;
        internal RawImage Image;
        internal RectTransform ImageRect;
        internal int Row;
        internal TMP_Text Status;
    }

    private void Awake()
    {
        active = this;
        try
        {
            if (!Chainloader.PluginInfos.TryGetValue("com.gunsaw.multiplayer", out var original) || original.Instance == null)
                throw new InvalidOperationException("Gunsaw Multiplayer is not loaded.");

            systemType = original.Instance.GetType().Assembly.GetType("GraffitiSystem", false);
            if (systemType == null)
                throw new MissingMemberException("GraffitiSystem is missing; this addon targets Gunsaw MP 0.5.3.");

            ExperimentalImageDecoder.RegisterManagedResolver();
            ResolveReflection();
            var scan = RequiredMethod("ScanLibrary");
            var rebuild = RequiredMethod("RebuildPicker");
            var begin = RequiredMethod("BeginPlacement");
            var first = RequiredMethod("FirstImagePath");

            harmony = new Harmony(PluginGuid);
            harmony.Patch(scan, prefix: new HarmonyMethod(PatchMethod(nameof(ScanLibraryPrefix))));
            harmony.Patch(rebuild, prefix: new HarmonyMethod(PatchMethod(nameof(RebuildPickerPrefix))));
            harmony.Patch(begin, prefix: new HarmonyMethod(PatchMethod(nameof(BeginPlacementPrefix))));
            harmony.Patch(first, prefix: new HarmonyMethod(PatchMethod(nameof(FirstImagePathPrefix))));
            InstallShotGuard();
            InstallTelekinesisGuard();

            // gsmp called initialize() during awake(). swoitch filesystemwatcher to rec. monitoring and clear its library.
            system = GetSystemInstance();
            if (system != null)
            {
                Attach(system);
                Reindex(system);
                ClearLoadedImages(system, true);
            }
            Logger.LogInfo("GunSpray v1.0.0 loaded.");
            Logger.LogDebug("GunSpray build 10, 25/11/2026 1720, RC3 build, stable.");
        }
        catch (Exception error)
        {
            Logger.LogError("Could not initialize GunSpray: " + error);
            try { harmony?.UnpatchSelf(); } catch { /* leave vanilla in control */ }
            active = null;
            enabled = false;
        }
    }

    // base mp mod prevents firing (gun or velvet spit) whgen placing sprays, not when the picker is open though. cannot intercept unitys input.getmousebuttondown, the ui needs it.
    private void InstallShotGuard()
    {
        try
        {
            var playerType = AccessTools.TypeByName("PlayerScript");
            localBodyType = AccessTools.TypeByName("BodyScript");
            if (playerType == null || localBodyType == null)
                throw new MissingMemberException("PlayerScript or BodyScript unavailable");

            const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
            localPlayerField = playerType.GetField("player", any);
            localPlayerProperty = playerType.GetProperty("player", any);
            playerBodyField = playerType.GetField("bodyScript", any);
            playerBodyProperty = playerType.GetProperty("bodyScript", any);
            if ((localPlayerField == null && localPlayerProperty == null) ||
                (playerBodyField == null && playerBodyProperty == null))
                throw new MissingMemberException("PlayerScript.player/bodyScript inaccessible");

            var patched = new List<string>();
            var weaponType = AccessTools.TypeByName("WeaponScript");
            if (weaponType != null)
            {
                weaponBodyField = AccessTools.Field(weaponType, "body");
                var shoot = AccessTools.Method(weaponType, "Shoot");
                if (shoot != null)
                {
                    harmony.Patch(shoot, prefix: new HarmonyMethod(PatchMethod(nameof(LocalShotGuardPrefix))));
                    patched.Add("WeaponScript.Shoot");
                }
            }

            var velvetType = AccessTools.TypeByName("VelvetScript");
            if (velvetType != null)
            {
                var shoot = AccessTools.Method(velvetType, "Shoot");
                if (shoot != null)
                {
                    harmony.Patch(shoot, prefix: new HarmonyMethod(PatchMethod(nameof(LocalShotGuardPrefix))));
                    patched.Add("VelvetScript.Shoot");
                }
            }
            if (patched.Count == 0)
                Logger.LogWarning("No gunfire-guard targets found. The graffiti menu may still fire weapons.");
            else
                Logger.LogDebug("Gunfire guard for GunSpray initialized: " + string.Join(", ", patched));
        }
        catch (Exception e)
        {
            // log error if gunfire guard fails to load, but dont break everything else. Thanks Johnny!
            Logger.LogWarning("Could not initialize GunSpray gunfire guard: " + e);
        }
    }

    private static bool LocalShotGuardPrefix(object __instance)
    {
        if (active == null || active.system == null) return true;
        try
        {
            // run only when menu open, never in normal gameplay
            if (!(bool)active.pickerOpenField.GetValue(active.system)) return true;
            return !active.IsLocalWeaponOwner(__instance);
        }
        catch (Exception e)
        {
            if (!active.shotGuardWarningLogged)
            {
                active.shotGuardWarningLogged = true;
                active.Logger.LogWarning("Gunfire guard check failed; allowing shots (once): " + e.Message);
            }
            return true; // fail open 2 avoid stopping unrelated weapon fire
        }
    }

    private bool IsLocalWeaponOwner(object shooter)
    {
        if (shooter == null) return false;
        var player = localPlayerField != null ? localPlayerField.GetValue(null) :
                     localPlayerProperty?.GetValue(null, null);
        if (player == null) return false;
        var localBody = playerBodyField != null ? playerBodyField.GetValue(player) :
                        playerBodyProperty?.GetValue(player, null);
        if (localBody == null) return false;

        // weaponscript.body used by gsmp
        object owner = null;
        if (weaponBodyField != null && weaponBodyField.DeclaringType.IsInstanceOfType(shooter))
            owner = weaponBodyField.GetValue(shooter);

        // velvetscript and other weird zany gun setups can use bodyscript ancestor
        if (owner == null && shooter is Component component)
            owner = component.GetComponentInParent(localBodyType);

        return owner != null && (ReferenceEquals(owner, localBody) || owner.Equals(localBody));
    }

    // block telekines when graffiti picker is open.
    private void InstallTelekinesisGuard()
    {
        try
        {
            var levitatorType = AccessTools.TypeByName("LevitatorScript");
            if (levitatorType == null)
                throw new MissingMemberException("LevitatorScript unavailable");

            const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            levitatorBodyField = levitatorType.GetField("refBody", any);
            levitatorBodyProperty = levitatorType.GetProperty("refBody", any);
            levitatorHeldObjectField = levitatorType.GetField("currentlyLevitating", any);
            levitatorUnGrabMethod = AccessTools.Method(levitatorType, "UnGrab");
            var tryGrab = AccessTools.Method(levitatorType, "TryGrab");
            var fixedUpdate = AccessTools.Method(levitatorType, "FixedUpdate");
            if ((levitatorBodyField == null && levitatorBodyProperty == null) ||
                levitatorHeldObjectField == null || levitatorUnGrabMethod == null ||
                tryGrab == null || fixedUpdate == null)
                throw new MissingMemberException("LevitatorScript members needed for the local input guard are missing");

            var multiplayerAssembly = systemType.Assembly;
            var avatarManager = multiplayerAssembly.GetType("NetworkAvatarManager", false);
            var npcReplication = multiplayerAssembly.GetType("NpcReplication", false);
            var remotePlayerGrab = avatarManager == null ? null : AccessTools.Method(avatarManager, "TryGrabRemotePlayer");
            var remoteCorpseGrab = npcReplication == null ? null : AccessTools.Method(npcReplication, "TryGrabClientCorpse");
            if (remotePlayerGrab == null || remoteCorpseGrab == null)
                throw new MissingMemberException("Gunsaw Multiplayer's additional telekinesis grab handlers are missing");

            var first = Priority.First;
            harmony.Patch(tryGrab, prefix: new HarmonyMethod(PatchMethod(nameof(LocalLevitatorTryGrabPrefix))) { priority = first });
            harmony.Patch(fixedUpdate, prefix: new HarmonyMethod(PatchMethod(nameof(LocalLevitatorTickPrefix))) { priority = first });
            // gsmp calls grab handlers from trygrab postfix even if og trygrab skips
            harmony.Patch(remotePlayerGrab, prefix: new HarmonyMethod(PatchMethod(nameof(LocalLevitatorExtraGrabPrefix))) { priority = first });
            harmony.Patch(remoteCorpseGrab, prefix: new HarmonyMethod(PatchMethod(nameof(LocalLevitatorExtraGrabPrefix))) { priority = first });
            Logger.LogDebug("Telekines guard initialized: LevitatorScript.TryGrab/FixedUpdate and multiplayer grab handlers.");
        }
        catch (Exception e)
        {
            Logger.LogWarning("Could not initialize GunSpray telekines guard: " + e);
        }
    }

    private static bool LocalLevitatorTryGrabPrefix(object __instance) => !ShouldBlockLocalTelekinesis(__instance);

    private static bool LocalLevitatorExtraGrabPrefix(object __0) => !ShouldBlockLocalTelekinesis(__0);

    private static bool LocalLevitatorTickPrefix(object __instance)
    {
        if (!ShouldBlockLocalTelekinesis(__instance)) return true;
        try
        {
            // release existing grab before skipping levitation physics tick
            if (active.levitatorHeldObjectField.GetValue(__instance) != null)
                active.levitatorUnGrabMethod.Invoke(__instance, null);
        }
        catch (Exception e)
        {
            active.LogTelekinesisGuardWarning(e);
        }
        return false;
    }

    private static bool ShouldBlockLocalTelekinesis(object levitator)
    {
        if (active == null || active.system == null || levitator == null) return false;
        try
        {
            if (!(bool)active.pickerOpenField.GetValue(active.system)) return false;
            return active.IsLocalLevitatorOwner(levitator);
        }
        catch (Exception e)
        {
            active.LogTelekinesisGuardWarning(e);
            return false;
        }
    }

    private void LogTelekinesisGuardWarning(Exception error)
    {
        if (telekinesisGuardWarningLogged) return;
        telekinesisGuardWarningLogged = true;
        Logger.LogWarning("Telekinesis picker guard failed; allowing normal telekinesis (once): " + error.Message);
    }

    private bool IsLocalLevitatorOwner(object levitator)
    {
        var player = localPlayerField != null ? localPlayerField.GetValue(null) :
                     localPlayerProperty?.GetValue(null, null);
        if (player == null) return false;
        var localBody = playerBodyField != null ? playerBodyField.GetValue(player) :
                        playerBodyProperty?.GetValue(player, null);
        if (localBody == null) return false;

        var owner = levitatorBodyField != null ? levitatorBodyField.GetValue(levitator) :
                    levitatorBodyProperty?.GetValue(levitator, null);
        if (owner == null && levitator is Component component)
            owner = component.GetComponentInParent(localBodyType);
        return owner != null && (ReferenceEquals(owner, localBody) || owner.Equals(localBody));
    }

    private MethodInfo PatchMethod(string name) =>
        typeof(GunSprayPlugin).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);

    private MethodInfo RequiredMethod(string name) =>
        AccessTools.Method(systemType, name) ?? throw new MissingMethodException(systemType.FullName, name);

    private FieldInfo RequiredField(string name) =>
        AccessTools.Field(systemType, name) ?? throw new MissingFieldException(systemType.FullName, name);

    private void ResolveReflection()
    {
        libraryField = RequiredField("library");
        pickerContentField = RequiredField("pickerContent");
        pickerRootField = RequiredField("pickerRoot");
        pickerScrollField = RequiredField("pickerScroll");
        pickerDirtyField = RequiredField("pickerDirty");
        selectedPathField = RequiredField("selectedPath");
        folderField = RequiredField("folder");
        watcherField = RequiredField("libraryWatcher");
        placingField = RequiredField("placing");
        pickerOpenField = RequiredField("pickerOpen");
        pickerItemsField = RequiredField("pickerItems");
        encodeTexture = RequiredMethod("EncodeTexture");
        checkSourceDimensions = RequiredMethod("IsSourceImageSizeAllowed");
        cancelMethod = RequiredMethod("Cancel");
        libraryImageType = systemType.GetNestedType("LibraryImage", BindingFlags.NonPublic);
        if (libraryImageType == null)
            throw new MissingMemberException("LibraryImage nested class not found.");
        timestampField = AccessTools.Field(libraryImageType, "Timestamp") ??
                         throw new MissingFieldException("LibraryImage.Timestamp");
        destroyLibraryImage = AccessTools.Method(libraryImageType, "Destroy") ??
                              throw new MissingMethodException("LibraryImage.Destroy");
    }

    private object GetSystemInstance() => RequiredField("instance").GetValue(null);

    private void Attach(object instance)
    {
        system = instance;
        rootFolder = (string)folderField.GetValue(instance);
        if (string.IsNullOrEmpty(rootFolder)) return; // hjeadless mode, no local library or ui required
        rootFolder = Path.GetFullPath(rootFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrEmpty(currentFolder)) currentFolder = rootFolder;
        Directory.CreateDirectory(rootFolder);

        var watcher = watcherField.GetValue(instance) as FileSystemWatcher;
        if (watcher != null && !watcher.IncludeSubdirectories)
        {
            watcher.EnableRaisingEvents = false;
            watcher.IncludeSubdirectories = true;
            watcher.EnableRaisingEvents = true;
        }
    }

    private static bool ScanLibraryPrefix(object __instance)
    {
        if (active == null) return true;
        try
        {
            active.Reindex(__instance);
            return false;
        }
        catch (Exception e)
        {
            active.Logger.LogError("Indexing failed: " + e);
            // dear god never run vanilla scanner on top of this. it would cause issues.
            return false;
        }
    }

    private static bool RebuildPickerPrefix(object __instance)
    {
        if (active == null) return true;
        try
        {
            active.BuildPicker(__instance);
            return false;
        }
        catch (Exception e)
        {
            active.Logger.LogError("Folder picker failed: " + e);
            // dont repeat shitty build every frame
            active.pickerDirtyField.SetValue(__instance, false);
            return false;
        }
    }

    private static bool BeginPlacementPrefix(object __instance, string path)
    {
        if (active == null) return true;
        if (string.IsNullOrEmpty(path))
        {
            if (active.suppressEmptySelectionOnRelease)
            {
                active.suppressEmptySelectionOnRelease = false;
                return false; // do not interpret folder click as clicking a spray
            }
            return true; //retain og warning about no images if well, no images.
        }

        active.suppressEmptySelectionOnRelease = false;
        try
        {
            if (active.indexed.ContainsKey(path)) active.EnsureLoaded(__instance, path);
        }
        catch (Exception e)
        {
            active.Logger.LogWarning("Unable to load selected spray: " + e.Message);
        }
        return true; // og handles every thing else from select preview to place
    }

    private static bool FirstImagePathPrefix(ref string __result)
    {
        if (active == null) return true;
        __result = active.indexed.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        return false;
    }

    private void Reindex(object instance)
    {
        if (system == null || !ReferenceEquals(system, instance)) Attach(instance);
        if (string.IsNullOrEmpty(rootFolder)) return;

        var files = new List<IndexedFile>();
        var discoveredFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rootFolder };
        var dirs = new Stack<string>();
        dirs.Push(rootFolder);
        indexingErrorCount = 0;
        scanProblems.Clear();

        while (dirs.Count > 0)
        {
            var dir = dirs.Pop();
            string[] subdirs;
            string[] localFiles;
            try
            {
                subdirs = Directory.GetDirectories(dir);
                localFiles = Directory.GetFiles(dir);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                RecordScanProblem(dir, e);
                continue;
            }

            Array.Sort(subdirs, StringComparer.OrdinalIgnoreCase);
            // fix ordering so it stays a to z
            for (int i = subdirs.Length - 1; i >= 0; i--)
            {
                var child = subdirs[i];
                try
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                    discoveredFolders.Add(child);
                    dirs.Push(child);
                }
                catch (IOException e) { RecordScanProblem(child, e); }
                catch (UnauthorizedAccessException e) { RecordScanProblem(child, e); }
            }

            foreach (var file in localFiles)
            {
                if (!IsSupported(file)) continue;
                try
                {
                    var info = new FileInfo(file);
                    // include sprays in menu even if too big or empty. they will be marked as invalid instead of not showing up, to allow users to diagnose the issue at hand.
                    files.Add(new IndexedFile { Path = file, Modified = info.LastWriteTimeUtc, Length = info.Length });
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    RecordScanProblem(file, e);
                }
            }
        }

        files.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path));
        var totalCandidates = files.Count;
        if (files.Count > MaxIndexedImages) files.RemoveRange(MaxIndexedImages, files.Count - MaxIndexedImages);

        indexed.Clear();
        foreach (var image in files) indexed[image.Path] = image;
        folders.Clear();
        foreach (var dir in discoveredFolders) folders.Add(dir);
        if (!folders.Contains(currentFolder)) currentFolder = rootFolder;

        // clear unity sprite cache if spray image deleted
        ClearLoadedImages(instance, false);
        foreach (var path in thumbnailCache.Keys.ToArray())
        {
            if (!indexed.TryGetValue(path, out var info) || info.Modified != thumbnailCache[path].Modified)
                EvictThumbnail(path);
        }
        failedThumbnails.Clear();
        pickerDirtyField.SetValue(instance, true);
        if (totalCandidates > MaxIndexedImages)
            Logger.LogWarning("Graffiti folder contains " + totalCandidates + " supported image files; first 512 alphabetically indexed.");
        if (indexingErrorCount > 0)
        {
            Logger.LogWarning("Could not scan " + indexingErrorCount + " graffiti paths.");
            foreach (var example in scanProblems)
                Logger.LogWarning("  Scan issue: " + example);
        }
        Logger.LogDebug("Indexed " + indexed.Count + " graffiti files in " + folders.Count + " folders.");
    }

    private void RecordScanProblem(string path, Exception e)
    {
        indexingErrorCount++;
        // dont shit the bed in the log.
        if (scanProblems.Count < 4)
            scanProblems.Add(Relative(path) + " (" + e.GetType().Name + "): " + e.Message);
    }

    private static bool IsSupported(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }

    private IDictionary GetLibrary(object instance) => (IDictionary)libraryField.GetValue(instance);

    private void ClearLoadedImages(object instance, bool clearAll)
    {
        var library = GetLibrary(instance);
        var placing = (bool)placingField.GetValue(instance);
        var selected = (string)selectedPathField.GetValue(instance);
        foreach (var key in library.Keys.Cast<object>().OfType<string>().ToArray())
        {
            if (!clearAll && indexed.TryGetValue(key, out var file))
            {
                if (placing && string.Equals(key, selected, StringComparison.OrdinalIgnoreCase)) continue;
                var existingTimestamp = (DateTime)timestampField.GetValue(library[key]);
                if (existingTimestamp == file.Modified) continue;
            }
            DestroyLibraryImage(library[key]);
            library.Remove(key);
        }
        if (!clearAll && selected != null && !indexed.ContainsKey(selected))
        {
            cancelMethod.Invoke(instance, null);
            selectedPathField.SetValue(instance, null);
        }
    }

    private void DestroyLibraryImage(object image)
    {
        if (image == null) return;
        try { destroyLibraryImage.Invoke(image, null); }
        catch (Exception e) { Logger.LogWarning("Could not free old graffiti image: " + e.Message); }
    }

    private bool EnsureLoaded(object instance, string path)
    {
        if (!indexed.TryGetValue(path, out var file)) return false;
        var library = GetLibrary(instance);
        if (library.Contains(path))
        {
            var timestamp = (DateTime)timestampField.GetValue(library[path]);
            if (timestamp == File.GetLastWriteTimeUtc(path)) return true;
            DestroyLibraryImage(library[path]);
            library.Remove(path);
        }

        Texture2D texture = null;
        Sprite sprite = null;
        try
        {
            // avoid loading huge files
            var length = new FileInfo(path).Length;
            if (length == 0 || length > MaxSourceBytes) return false;
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0 || bytes.Length > MaxSourceBytes) return false;

            if (ExperimentalImageDecoder.IsExperimental(path))
            {
                if (!ExperimentalImageDecoder.TryGetDimensions(bytes, path, out var width, out var height) ||
                    width > MaxImageDimension || height > MaxImageDimension) return false;
                texture = ExperimentalImageDecoder.DecodeFirstFrame(bytes, path);
            }
            else
            {
                if (!(bool)checkSourceDimensions.Invoke(null, new object[] { bytes })) return false;
                texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!ImageConversion.LoadImage(texture, bytes) || texture.width > MaxImageDimension ||
                    texture.height > MaxImageDimension) return false;
            }

            var encoded = (byte[])encodeTexture.Invoke(null, new object[] { texture });
            if (encoded == null) return false;
            sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f), 100f);
            var modified = File.GetLastWriteTimeUtc(path);
            var entry = Activator.CreateInstance(libraryImageType,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null,
                new object[] { encoded, texture, sprite, modified }, null);
            library[path] = entry;
            texture = null; // libraryimage
            sprite = null;

            // keep at most one full size source spray in cache
            foreach (var other in library.Keys.Cast<object>().OfType<string>().Where(x =>
                         !string.Equals(x, path, StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                DestroyLibraryImage(library[other]);
                library.Remove(other);
            }
            return true;
        }
        finally
        {
            if (sprite != null) Object.Destroy(sprite);
            if (texture != null) Object.Destroy(texture);
        }
    }

    private void BuildPicker(object instance)
    {
        if (system == null || !ReferenceEquals(system, instance)) Attach(instance);
        if (string.IsNullOrEmpty(rootFolder)) return;
        var picker = pickerRootField.GetValue(instance) as GameObject;
        var content = pickerContentField.GetValue(instance) as GameObject;
        var scrollRect = pickerScrollField.GetValue(instance) as ScrollRect;
        if (picker == null || content == null || scrollRect == null) return;
        rootUi = picker;
        contentUi = content;
        scroll = scrollRect;
        InitializeNavigationUi(picker, scrollRect);
        pickerDirtyField.SetValue(instance, false);

        selectionOutlines.Clear();
        imageTiles.Clear();
        var pickerItems = pickerItemsField.GetValue(instance) as IDictionary;
        pickerItems?.Clear();
        for (var i = content.transform.childCount - 1; i >= 0; i--)
            Object.Destroy(content.transform.GetChild(i).gameObject);

        var childFolders = folders.Where(x => !string.Equals(x, currentFolder, StringComparison.OrdinalIgnoreCase) &&
                                               string.Equals(Path.GetDirectoryName(x), currentFolder, StringComparison.OrdinalIgnoreCase))
                                  .OrderBy(x => Path.GetFileName(x), StringComparer.OrdinalIgnoreCase).ToList();
        var childImages = indexed.Keys.Where(x => string.Equals(Path.GetDirectoryName(x), currentFolder,
                                                     StringComparison.OrdinalIgnoreCase))
                                      .OrderBy(x => Path.GetFileName(x), StringComparer.OrdinalIgnoreCase).ToList();
        var entries = childFolders.Count + childImages.Count;
        var rows = Math.Max(1, Mathf.CeilToInt(entries / (float)Columns));
        var rect = (RectTransform)content.transform;
        rect.sizeDelta = new Vector2(840f, rows * CellStep);
        rect.anchoredPosition = Vector2.zero;

        var index = 0;
        foreach (var folderPath in childFolders)
            MakeFolderTile(content.transform, folderPath, index++);
        foreach (var filePath in childImages)
            MakeImageTile(content.transform, filePath, index++);

        var relative = Relative(currentFolder);
        pathLabel.text = relative.Length == 0 ? "Graffiti /" : "Graffiti / " + relative;
        // pad so it doesnt overlap back button
        countLabel.text = indexed.Count + "/" + MaxIndexedImages + " files | " + childFolders.Count +
                          " folders | PNG/JPG/GIF/WebP | max 1280px/axis, 1MiB/file";
        backImage.color = currentFolder == rootFolder ? new Color(.15f, .17f, .17f, 1f) : new Color(.28f, .31f, .31f, 1f);
        scrollRect.verticalNormalizedPosition = 1f;
        RefreshSelectionOutline((string)selectedPathField.GetValue(instance));
    }

    private void InitializeNavigationUi(GameObject picker, ScrollRect scrollRect)
    {
        if (navigationInitialized && picker == rootUi && pathLabel != null) return;
        // get font from game ui
        var existingTitle = picker.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault();
        uiFont = existingTitle != null && existingTitle.font != null ? existingTitle.font : TMP_Settings.defaultFontAsset;
        uiFontMaterial = existingTitle != null ? existingTitle.fontSharedMaterial : null;
        // pad path bar underneath graffitti title
        var viewport = scrollRect.viewport;
        viewport.sizeDelta = new Vector2(850f, 625f);
        viewport.anchoredPosition = new Vector2(0f, -44f);

        var back = new GameObject("Folder Back Button", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(EventTrigger));
        back.transform.SetParent(picker.transform, false);
        SetRect((RectTransform)back.transform, new Vector2(-365f, 293f), new Vector2(115f, 34f));
        backImage = back.GetComponent<Image>();
        backImage.color = new Color(.28f, .31f, .31f, 1f);
        MakeLabel(back.transform, "BACK", Vector2.zero, new Vector2(105f, 26f), 15f, TextAlignmentOptions.Center);
        RegisterClick(back, GoBack);

        pathLabel = MakeLabel(picker.transform, "Graffiti /", new Vector2(35f, 300f),
                              new Vector2(600f, 28f), 16f, TextAlignmentOptions.Left);
        pathLabel.overflowMode = TextOverflowModes.Ellipsis;
        // keep info inside column. 765wide rectangle etxended left over back button. (occupying -423 -308)
        countLabel = MakeLabel(picker.transform, "", new Vector2(35f, 278f),
                               new Vector2(600f, 20f), 10f, TextAlignmentOptions.Left);
        countLabel.enableWordWrapping = false;
        countLabel.overflowMode = TextOverflowModes.Ellipsis;
        countLabel.color = new Color(.72f, .75f, .76f, 1f);
        navigationInitialized = true;
    }

    private void MakeFolderTile(Transform parent, string path, int index)
    {
        var tile = MakeTile(parent, "Folder: " + Path.GetFileName(path), index);
        var tab = new GameObject("Folder Tab", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        tab.transform.SetParent(tile.transform, false);
        SetRect((RectTransform)tab.transform, new Vector2(-20f, 13f), new Vector2(35f, 14f));
        var tabImage = tab.GetComponent<Image>();
        tabImage.color = new Color(.84f, .69f, .34f, 1f);
        tabImage.raycastTarget = false;
        var body = new GameObject("Folder Body", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        body.transform.SetParent(tile.transform, false);
        SetRect((RectTransform)body.transform, new Vector2(0f, -4f), new Vector2(73f, 42f));
        var bodyImage = body.GetComponent<Image>();
        bodyImage.color = new Color(.93f, .76f, .36f, 1f);
        bodyImage.raycastTarget = false;
        MakeLabel(tile.transform, Path.GetFileName(path), new Vector2(0f, -39f),
                  new Vector2(98f, 28f), 12f, TextAlignmentOptions.Center);
        RegisterClick(tile, () => Navigate(path));
        RegisterScroll(tile);
    }

    private void MakeImageTile(Transform parent, string path, int index)
    {
        var tile = MakeTile(parent, "Graffiti: " + Path.GetFileName(path), index);
        var imageGo = new GameObject("Thumbnail", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
        imageGo.transform.SetParent(tile.transform, false);
        var imageRect = (RectTransform)imageGo.transform;
        SetRect(imageRect, new Vector2(0f, 9f), new Vector2(ThumbnailSize, ThumbnailSize - 16));
        var rawImage = imageGo.GetComponent<RawImage>();
        rawImage.color = Color.white;
        rawImage.raycastTarget = false;
        MakeLabel(tile.transform, Path.GetFileNameWithoutExtension(path), new Vector2(0f, -40f),
                  new Vector2(100f, 26f), 11f, TextAlignmentOptions.Center);
        var status = MakeLabel(tile.transform, "", new Vector2(0f, 9f),
                               new Vector2(96f, 50f), 11f, TextAlignmentOptions.Center);
        status.color = new Color(1f, .65f, .42f, 1f);
        status.gameObject.SetActive(false);
        if (indexed.TryGetValue(path, out var info))
        {
            if (info.Length == 0) ShowInvalidStatus(status, "EMPTY FILE");
            else if (info.Length > MaxSourceBytes) ShowInvalidStatus(status, ">1MiB");
            else if (failedThumbnails.TryGetValue(path, out var cachedFailure))
                ShowInvalidStatus(status, cachedFailure);
        }
        var outline = tile.GetComponent<Outline>();
        selectionOutlines[path] = outline;
        imageTiles.Add(new Tile { Path = path, Image = rawImage, ImageRect = imageRect,
                                  Row = index / Columns, Status = status });
        RegisterEnter(tile, () => ChooseImage(path));
        RegisterClick(tile, () => ChooseImage(path));
        RegisterScroll(tile);
    }

    private static void ShowInvalidStatus(TMP_Text label, string reason)
    {
        if (label == null) return;
        label.text = "INVALID / " + (string.IsNullOrEmpty(reason) ? "CANNOT LOAD" : reason);
        label.gameObject.SetActive(true);
    }

    private GameObject MakeTile(Transform parent, string name, int index)
    {
        var tile = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Outline), typeof(EventTrigger));
        tile.transform.SetParent(parent, false);
        var rect = (RectTransform)tile.transform;
        SetRect(rect, new Vector2((index % Columns - (Columns - 1) * .5f) * CellStep,
                                   -7f - index / Columns * CellStep), new Vector2(104f, 104f));
        rect.anchorMin = rect.anchorMax = new Vector2(.5f, 1f);
        rect.pivot = new Vector2(.5f, 1f);
        tile.GetComponent<Image>().color = new Color(.16f, .20f, .20f, .95f);
        var outline = tile.GetComponent<Outline>();
        outline.effectColor = new Color(.83f, .66f, .12f, 0f);
        outline.effectDistance = new Vector2(3f, -3f);
        return tile;
    }

    private TMP_Text MakeLabel(Transform parent, string value, Vector2 pos, Vector2 size,
                               float fontSize, TextAlignmentOptions alignment)
    {
        var go = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);
        SetRect((RectTransform)go.transform, pos, size);
        var tmp = go.GetComponent<TextMeshProUGUI>();
        tmp.font = uiFont != null ? uiFont : TMP_Settings.defaultFontAsset;
        if (uiFontMaterial != null) tmp.fontSharedMaterial = uiFontMaterial;
        tmp.fontSize = fontSize;
        tmp.color = Color.white;
        tmp.text = value;
        tmp.alignment = alignment;
        tmp.enableWordWrapping = true;
        tmp.overflowMode = TextOverflowModes.Ellipsis;
        tmp.richText = false; // avoid interpreting folder or file names as tmp
        tmp.raycastTarget = false;
        return tmp;
    }

    private static void SetRect(RectTransform rect, Vector2 pos, Vector2 size)
    {
        rect.anchorMin = rect.anchorMax = new Vector2(.5f, .5f);
        rect.pivot = new Vector2(.5f, .5f);
        rect.anchoredPosition = pos;
        rect.sizeDelta = size;
    }

    private static void RegisterClick(GameObject tile, Action action)
    {
        var entry = new EventTrigger.Entry { eventID = EventTriggerType.PointerClick };
        //pointerclick is used for right clicks as well, leave em entirely to goback() rather than opening a folder or selecting a spray by mistake.
        entry.callback.AddListener(data =>
        {
            if (data is PointerEventData pointer && pointer.button == PointerEventData.InputButton.Left)
                action();
        });
        tile.GetComponent<EventTrigger>().triggers.Add(entry);
    }

    private static void RegisterEnter(GameObject tile, Action action)
    {
        var entry = new EventTrigger.Entry { eventID = EventTriggerType.PointerEnter };
        entry.callback.AddListener(_ => action());
        tile.GetComponent<EventTrigger>().triggers.Add(entry);
    }

    private void RegisterScroll(GameObject tile)
    {
        var entry = new EventTrigger.Entry { eventID = EventTriggerType.Scroll };
        entry.callback.AddListener(data => scroll?.OnScroll((PointerEventData)data));
        tile.GetComponent<EventTrigger>().triggers.Add(entry);
    }

    private void Navigate(string dest)
    {
        if (system == null || !folders.Contains(dest)) return;
        currentFolder = dest;
        selectedPathField.SetValue(system, null);
        suppressEmptySelectionOnRelease = true;
        pickerDirtyField.SetValue(system, true);
    }

    private void GoBack()
    {
        if (string.Equals(currentFolder, rootFolder, StringComparison.OrdinalIgnoreCase)) return;
        var parent = Path.GetDirectoryName(currentFolder);
        Navigate(parent != null && folders.Contains(parent) ? parent : rootFolder);
    }

    private void ChooseImage(string path)
    {
        if (system == null || !indexed.ContainsKey(path)) return;
        selectedPathField.SetValue(system, path);
        suppressEmptySelectionOnRelease = false;
        RefreshSelectionOutline(path);
        // full images are not encoded or generated on hover, only after beginplacement() and user releasing spray key.
    }

    private void RefreshSelectionOutline(string path)
    {
        foreach (var pair in selectionOutlines)
        {
            if (pair.Value == null) continue;
            var selected = string.Equals(pair.Key, path, StringComparison.OrdinalIgnoreCase);
            pair.Value.effectColor = new Color(.83f, .66f, .12f, selected ? 1f : 0f);
        }
    }

    private string Relative(string path)
    {
        if (string.IsNullOrEmpty(rootFolder) || string.Equals(path, rootFolder, StringComparison.OrdinalIgnoreCase))
            return "";
        return path.Substring(rootFolder.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   .Replace('\\', '/');
    }

    private void Update()
    {
        if (system == null)
        {
            var instance = systemType == null ? null : GetSystemInstance();
            if (instance != null)
            {
                Attach(instance);
                Reindex(instance);
                ClearLoadedImages(instance, true);
            }
            return;
        }
        if (string.IsNullOrEmpty(rootFolder) || !(bool)pickerOpenField.GetValue(system)) return;
        // right click to go back one folder
        if (Input.GetMouseButtonDown(1)) GoBack();
        ProcessVisibleThumbnails();
    }

    private void ProcessVisibleThumbnails()
    {
        if (scroll == null || imageTiles.Count == 0) return;
        var contentHeight = ((RectTransform)contentUi.transform).sizeDelta.y;
        var viewportHeight = scroll.viewport.rect.height;
        var top = (1f - scroll.verticalNormalizedPosition) * Mathf.Max(0f, contentHeight - viewportHeight);
        var startRow = Mathf.Max(0, Mathf.FloorToInt(top / CellStep) - 1);
        var endRow = Mathf.CeilToInt((top + viewportHeight) / CellStep) + 1;
        var built = 0;
        foreach (var tile in imageTiles)
        {
            if (tile.Image == null) continue;
            var visible = tile.Row >= startRow && tile.Row <= endRow;
            if (!visible)
            {
                if (tile.Image.texture != null) tile.Image.texture = null;
                continue;
            }
            if (thumbnailCache.TryGetValue(tile.Path, out var thumb))
            {
                tile.Image.texture = thumb.Texture;
                thumb.LastUsed = Time.frameCount;
                FitImage(tile, thumb.Texture.width, thumb.Texture.height);
            }
            else if (!failedThumbnails.ContainsKey(tile.Path) && built < ThumbnailBuildsPerFrame)
            {
                built++;
                var texture = MakeThumbnail(tile.Path, out var failureReason);
                if (texture == null)
                {
                    failedThumbnails[tile.Path] = failureReason;
                    ShowInvalidStatus(tile.Status, failureReason);
                    continue;
                }
                if (tile.Status != null) tile.Status.gameObject.SetActive(false);
                thumbnailCache[tile.Path] = new Thumb
                {
                    Texture = texture,
                    Modified = indexed[tile.Path].Modified,
                    LastUsed = Time.frameCount
                };
                tile.Image.texture = texture;
                FitImage(tile, texture.width, texture.height);
            }
        }
        TrimThumbnailCache();
    }

    private static void FitImage(Tile tile, int w, int h)
    {
        if (w < 1 || h < 1) return;
        var scale = Mathf.Min(ThumbnailSize / (float)w, (ThumbnailSize - 16) / (float)h);
        tile.ImageRect.sizeDelta = new Vector2(w * scale, h * scale);
    }

    private Texture2D MakeThumbnail(string path, out string failureReason)
    {
        failureReason = "CANNOT LOAD";
        if (!indexed.TryGetValue(path, out var file)) return null;
        if (file.Length == 0) { failureReason = "EMPTY FILE"; return null; }
        if (file.Length > MaxSourceBytes) { failureReason = ">1MiB"; return null; }
        Texture2D source = null, resized = null;
        RenderTexture render = null;
        var previous = RenderTexture.active;
        try
        {
            // recheck files, which may be overwritten after last scan
            var length = new FileInfo(path).Length;
            if (length == 0) { failureReason = "EMPTY FILE"; return null; }
            if (length > MaxSourceBytes) { failureReason = ">1MiB"; return null; }
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0) { failureReason = "EMPTY FILE"; return null; }
            if (bytes.Length > MaxSourceBytes) { failureReason = ">1MiB"; return null; }
            if (ExperimentalImageDecoder.IsExperimental(path))
            {
                if (!ExperimentalImageDecoder.TryGetDimensions(bytes, path, out var sourceWidth, out var sourceHeight))
                {
                    failureReason = "INVALID HEADER";
                    return null;
                }
                if (sourceWidth > MaxImageDimension || sourceHeight > MaxImageDimension)
                {
                    failureReason = ">1280px";
                    return null;
                }
                source = ExperimentalImageDecoder.DecodeFirstFrame(bytes, path);
            }
            else
            {
                if (!(bool)checkSourceDimensions.Invoke(null, new object[] { bytes }))
                {
                    failureReason = ">1280px";
                    return null;
                }
                source = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!ImageConversion.LoadImage(source, bytes, true)) return null;
                if (source.width > MaxImageDimension || source.height > MaxImageDimension)
                {
                    failureReason = ">1280px";
                    return null;
                }
            }
            var scale = Math.Min(1f, Math.Min(ThumbnailSize / (float)source.width,
                                             (ThumbnailSize - 16) / (float)source.height));
            var width = Math.Max(1, Mathf.RoundToInt(source.width * scale));
            var height = Math.Max(1, Mathf.RoundToInt(source.height * scale));
            render = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(source, render);
            RenderTexture.active = render;
            resized = new Texture2D(width, height, TextureFormat.RGBA32, false);
            resized.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            resized.Apply(false, true);
            var finished = resized;
            resized = null;
            return finished;
        }
        catch (Exception e)
        {
            if (ExperimentalImageDecoder.IsExperimental(path)) failureReason = "DECODER ERROR";
            Logger.LogWarning("Skipping preview '" + Relative(path) + "': " + e.Message);
            return null;
        }
        finally
        {
            RenderTexture.active = previous;
            if (render != null) RenderTexture.ReleaseTemporary(render);
            if (source != null) Object.Destroy(source);
            if (resized != null) Object.Destroy(resized);
        }
    }

    private void TrimThumbnailCache()
    {
        if (thumbnailCache.Count <= ThumbnailCacheLimit) return;
        var visible = new HashSet<string>(imageTiles.Where(x => x.Image != null && x.Image.texture != null)
                                                    .Select(x => x.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var entry in thumbnailCache.OrderBy(x => x.Value.LastUsed).ToArray())
        {
            if (thumbnailCache.Count <= ThumbnailCacheLimit) break;
            if (!visible.Contains(entry.Key)) EvictThumbnail(entry.Key);
        }
    }

    private void EvictThumbnail(string path)
    {
        if (!thumbnailCache.TryGetValue(path, out var old)) return;
        foreach (var tile in imageTiles)
            if (string.Equals(tile.Path, path, StringComparison.OrdinalIgnoreCase) && tile.Image != null)
                tile.Image.texture = null;
        thumbnailCache.Remove(path);
        if (old.Texture != null) Object.Destroy(old.Texture);
    }

    private void OnDestroy()
    {
        if (active == this) active = null;
        try { harmony?.UnpatchSelf(); } catch { }
        foreach (var path in thumbnailCache.Keys.ToArray()) EvictThumbnail(path);
        // fully cached images r owned by gsmp, it will clean em up. restoring vanilla scanning during live gameplay is unsupported. need to restart after uninstalling.
    }
}
