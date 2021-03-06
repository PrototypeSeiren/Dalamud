using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using CheapLoc;
using Dalamud.Game.Text;
using Newtonsoft.Json;
using Serilog;

namespace Dalamud.Plugin
{
    internal class PluginRepository
    {
        private const string PluginMasterUrl = "https://dalamudplugins-1253720819.cos.ap-nanjing.myqcloud.com/pluginmaster.json";

        private readonly Dalamud dalamud;
        private string pluginDirectory;

        /// <summary>
        /// Initializes a new instance of the <see cref="PluginRepository"/> class.
        /// </summary>
        /// <param name="dalamud">The Dalamud instance.</param>
        /// <param name="pluginDirectory">The plugin directory path.</param>
        /// <param name="gameVersion">The current game version.</param>
        public PluginRepository(Dalamud dalamud, string pluginDirectory, string gameVersion)
        {
            this.dalamud = dalamud;
            this.pluginDirectory = pluginDirectory;

            this.ReloadPluginMasterAsync();
        }

        /// <summary>
        /// Values representing plugin initialization state.
        /// </summary>
        public enum InitializationState
        {
            /// <summary>
            /// State is unknown.
            /// </summary>
            Unknown,

            /// <summary>
            /// State is in progress.
            /// </summary>
            InProgress,

            /// <summary>
            /// State is successful.
            /// </summary>
            Success,

            /// <summary>
            /// State is failure.
            /// </summary>
            Fail,

            /// <summary>
            /// State is failure, for a 3rd party repo plugin.
            /// </summary>
            FailThirdRepo,
        }

        /// <summary>
        /// Gets the plugin master list of available plugins.
        /// </summary>
        public ReadOnlyCollection<PluginDefinition> PluginMaster { get; private set; }

        /// <summary>
        /// Gets the initialization state of the plugin repository.
        /// </summary>
        public InitializationState State { get; private set; }

        /// <summary>
        /// Reload the plugin master asynchronously in a task.
        /// </summary>
        public void ReloadPluginMasterAsync()
        {
            this.State = InitializationState.InProgress;

            Task.Run(() =>
            {
                this.PluginMaster = null;

                var allPlugins = new List<PluginDefinition>();

                var repos = this.dalamud.Configuration.ThirdRepoList.Where(x => x.IsEnabled).Select(x => x.Url)
                                .Prepend(PluginMasterUrl).ToArray();

                try
                {
                    using var client = new WebClient();

                    var repoNumber = 0;
                    foreach (var repo in repos)
                    {
                        Log.Information("[PLUGINR] Fetching repo: {0}", repo);

                        var data = client.DownloadString(repo);

                        var unsortedPluginMaster = JsonConvert.DeserializeObject<List<PluginDefinition>>(data);

                        foreach (var pluginDefinition in unsortedPluginMaster)
                        {
                            pluginDefinition.RepoNumber = repoNumber;
                        }

                        allPlugins.AddRange(unsortedPluginMaster);

                        repoNumber++;
                    }

                    this.PluginMaster = allPlugins.AsReadOnly();
                    this.State = InitializationState.Success;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Could not download PluginMaster");

                    this.State = repos.Length > 1 ? InitializationState.FailThirdRepo : InitializationState.Fail;
                }
            }).ContinueWith(t =>
            {
                if (t.IsFaulted)
                    this.State = InitializationState.Fail;
            });
        }

        /// <summary>
        /// Install a plugin.
        /// </summary>
        /// <param name="definition">The plugin definition.</param>
        /// <param name="enableAfterInstall">Whether the plugin should be immediately enabled.</param>
        /// <param name="isUpdate">Whether this install is an update.</param>
        /// <param name="fromTesting">Whether this install is flagged as testing.</param>
        /// <returns>Success or failure.</returns>
        public bool InstallPlugin(PluginDefinition definition, bool enableAfterInstall = true, bool isUpdate = false, bool fromTesting = false)
        {
            try
            {
                using var client = new WebClient();

                var outputDir = new DirectoryInfo(Path.Combine(this.pluginDirectory, definition.InternalName, fromTesting ? definition.TestingAssemblyVersion : definition.AssemblyVersion));
                var dllFile = new FileInfo(Path.Combine(outputDir.FullName, $"{definition.InternalName}.dll"));
                var disabledFile = new FileInfo(Path.Combine(outputDir.FullName, ".disabled"));
                var testingFile = new FileInfo(Path.Combine(outputDir.FullName, ".testing"));
                var wasDisabled = disabledFile.Exists;

                if (dllFile.Exists && enableAfterInstall)
                {
                    if (disabledFile.Exists)
                        disabledFile.Delete();

                    return this.dalamud.PluginManager.LoadPluginFromAssembly(dllFile, false, PluginLoadReason.Installer);
                }

                if (dllFile.Exists && !enableAfterInstall)
                {
                    return true;
                }

                try
                {
                    if (outputDir.Exists)
                        outputDir.Delete(true);
                    outputDir.Create();
                }
                catch
                {
                    // ignored, since the plugin may be loaded already
                }

                var path = Path.GetTempFileName();

                var doTestingDownload = false;
                if ((Version.TryParse(definition.TestingAssemblyVersion, out var testingAssemblyVer) || definition.IsTestingExclusive)
                    && fromTesting)
                {
                    doTestingDownload = testingAssemblyVer > Version.Parse(definition.AssemblyVersion) || definition.IsTestingExclusive;
                }

                var url = definition.DownloadLinkInstall;
                if (doTestingDownload)
                    url = definition.DownloadLinkTesting;
                else if (isUpdate)
                    url = definition.DownloadLinkUpdate;

                Log.Information("Downloading plugin to {0} from {1} doTestingDownload:{2} isTestingExclusive:{3}", path, url, doTestingDownload, definition.IsTestingExclusive);
                try
                {
                    client.DownloadFile(url, path);
                    Log.Information("Extracting to {0}", outputDir);
                    ZipFile.ExtractToDirectory(path, outputDir.FullName);
                }
                catch (Exception e)
                {
                    Log.Information(e, "Plugin download failed not hard, trying fastgit.");
                    url = Regex.Replace(url, @"^https:\/\/raw\.githubusercontent\.com", "https://raw.fastgit.org");
                    url = Regex.Replace(url, @"^https:\/\/(?:gitee|github)\.com\/(.*)?\/(.*)?\/raw", "https://raw.fastgit.org/$1/$2");
                    url = Regex.Replace(url, @"^https:\/\/github\.com\/(.*)?\/(.*)?\/releases\/download", "https://download.fastgit.org/$1/$2/releases/download/");
                    Log.Information("Downloading plugin to {0} from {1} doTestingDownload:{2} isTestingExclusive:{3}", path, url, doTestingDownload, definition.IsTestingExclusive);
                    client.DownloadFile(url, path);
                    Log.Information("Extracting to {0}", outputDir);
                    ZipFile.ExtractToDirectory(path, outputDir.FullName);
                }

                if (wasDisabled || !enableAfterInstall)
                {
                    disabledFile.Create().Close();
                    return true;
                }

                if (doTestingDownload)
                {
                    testingFile.Create().Close();
                }
                else
                {
                    if (testingFile.Exists)
                        testingFile.Delete();
                }

                return this.dalamud.PluginManager.LoadPluginFromAssembly(dllFile, false, PluginLoadReason.Installer);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Plugin download failed hard.");
                if (ex is ReflectionTypeLoadException typeLoadException)
                {
                    foreach (var exception in typeLoadException.LoaderExceptions)
                    {
                        Log.Error(exception, "LoaderException:");
                    }
                }

                return false;
            }
        }

        /// <summary>
        /// Update all plugins.
        /// </summary>
        /// <param name="dryRun">Perform a dry run of the update and skip the actual installation.</param>
        /// <returns>A tuple of whether the update was successful and the list of updated plugins.</returns>
        public (bool Success, List<PluginUpdateStatus> UpdatedPlugins) UpdatePlugins(bool dryRun = false)
        {
            Log.Information("Starting plugin update... dry:{0}", dryRun);

            var updatedList = new List<PluginUpdateStatus>();
            var hasError = false;

            try
            {
                var pluginsDirectory = new DirectoryInfo(this.pluginDirectory);
                foreach (var installed in pluginsDirectory.GetDirectories())
                {
                    try
                    {
                        var versions = installed.GetDirectories();

                        if (versions.Length == 0)
                        {
                            Log.Information("Has no versions: {0}", installed.FullName);
                            continue;
                        }

                        var sortedVersions = versions.OrderBy(dirInfo =>
                        {
                            var success = Version.TryParse(dirInfo.Name, out var version);
                            if (!success)
                            {
                                Log.Debug("Unparseable version: {0}", dirInfo.Name);
                            }

                            return version;
                        });
                        var latest = sortedVersions.Last();

                        var isEnabled = !File.Exists(Path.Combine(latest.FullName, ".disabled"));
                        if (!isEnabled && File.Exists(Path.Combine(latest.FullName, ".testing")))
                        {
                            // In case testing is installed, but stable is enabled
                            foreach (var version in versions)
                            {
                                if (!File.Exists(Path.Combine(version.FullName, ".disabled")))
                                {
                                    isEnabled = true;
                                    break;
                                }
                            }
                        }

                        if (!isEnabled)
                        {
                            Log.Verbose("Is disabled: {0}", installed.FullName);
                            continue;
                        }

                        var localInfoFile = new FileInfo(Path.Combine(latest.FullName, $"{installed.Name}.json"));

                        if (!localInfoFile.Exists)
                        {
                            Log.Information("Has no definition: {0}", localInfoFile.FullName);
                            continue;
                        }

                        var info = JsonConvert.DeserializeObject<PluginDefinition>(
                            File.ReadAllText(localInfoFile.FullName));

                        var remoteInfo = this.PluginMaster.FirstOrDefault(x => x.InternalName == info.InternalName);

                        if (remoteInfo == null)
                        {
                            Log.Information("Is not in pluginmaster: {0}", info.Name);
                            continue;
                        }

                        if (remoteInfo.DalamudApiLevel < PluginManager.DalamudApiLevel)
                        {
                            Log.Information("Has not applicable API level: {0}", info.Name);
                            continue;
                        }

                        Version.TryParse(remoteInfo.AssemblyVersion, out var remoteAssemblyVer);
                        Version.TryParse(info.AssemblyVersion, out var localAssemblyVer);

                        var testingAvailable = false;
                        if (!string.IsNullOrEmpty(remoteInfo.TestingAssemblyVersion))
                        {
                            Version.TryParse(remoteInfo.TestingAssemblyVersion, out var testingAssemblyVer);
                            testingAvailable = testingAssemblyVer > localAssemblyVer && this.dalamud.Configuration.DoPluginTest;
                        }

                        if (remoteAssemblyVer > localAssemblyVer || testingAvailable)
                        {
                            Log.Information("Eligible for update: {0}", remoteInfo.InternalName);

                            // DisablePlugin() below immediately creates a .disabled file anyway, but will fail
                            // with an exception if we try to do it twice in row like this

                            if (!dryRun)
                            {
                                var wasLoaded =
                                    this.dalamud.PluginManager.Plugins.Where(x => x.Definition != null).Any(
                                        x => x.Definition.InternalName == info.InternalName);

                                Log.Verbose("isEnabled: {0} / wasLoaded: {1}", isEnabled, wasLoaded);

                                // Try to disable plugin if it is loaded
                                if (wasLoaded)
                                {
                                    try
                                    {
                                        this.dalamud.PluginManager.DisablePlugin(info);
                                    }
                                    catch (Exception ex)
                                    {
                                        Log.Error(ex, "Plugin disable failed");
                                        // hasError = true;
                                    }
                                }

                                try
                                {
                                    // Just to be safe
                                    foreach (var sortedVersion in sortedVersions)
                                    {
                                        var disabledFile =
                                            new FileInfo(Path.Combine(sortedVersion.FullName, ".disabled"));
                                        if (!disabledFile.Exists)
                                            disabledFile.Create().Close();
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Log.Error(ex, "Plugin disable old versions failed");
                                }

                                var installSuccess = this.InstallPlugin(remoteInfo, isEnabled, true, testingAvailable);

                                if (!installSuccess)
                                {
                                    Log.Error("InstallPlugin failed.");
                                    hasError = true;
                                }

                                updatedList.Add(new PluginUpdateStatus
                                {
                                    InternalName = remoteInfo.InternalName,
                                    Name = remoteInfo.Name,
                                    Version = testingAvailable ? remoteInfo.TestingAssemblyVersion : remoteInfo.AssemblyVersion,
                                    WasUpdated = installSuccess,
                                });
                            }
                            else
                            {
                                updatedList.Add(new PluginUpdateStatus
                                {
                                    InternalName = remoteInfo.InternalName,
                                    Name = remoteInfo.Name,
                                    Version = testingAvailable ? remoteInfo.TestingAssemblyVersion : remoteInfo.AssemblyVersion,
                                    WasUpdated = true,
                                });
                            }
                        }
                        else
                        {
                            Log.Information("Up to date: {0}", remoteInfo.InternalName);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Could not update plugin: {0}", installed.FullName);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Plugin update failed.");
                hasError = true;
            }

            Log.Information("Plugin update OK.");

            return (!hasError, updatedList);
        }

        /// <summary>
        /// Print to chat any plugin updates and whether they were successful.
        /// </summary>
        /// <param name="updatedPlugins">The list of updated plugins.</param>
        /// <param name="header">The header text to send to chat prior to any update info.</param>
        public void PrintUpdatedPlugins(List<PluginUpdateStatus> updatedPlugins, string header)
        {
            if (updatedPlugins != null && updatedPlugins.Any())
            {
                this.dalamud.Framework.Gui.Chat.Print(header);
                foreach (var plugin in updatedPlugins)
                {
                    if (plugin.WasUpdated)
                    {
                        this.dalamud.Framework.Gui.Chat.Print(string.Format(Loc.Localize("DalamudPluginUpdateSuccessful", "    》 {0} updated to v{1}."), plugin.Name, plugin.Version));
                    }
                    else
                    {
                        this.dalamud.Framework.Gui.Chat.PrintChat(new XivChatEntry
                        {
                            MessageBytes = Encoding.UTF8.GetBytes(string.Format(Loc.Localize("DalamudPluginUpdateFailed", "    》 {0} update to v{1} failed."), plugin.Name, plugin.Version)),
                            Type = XivChatType.Urgent,
                        });
                    }
                }
            }
        }

        /// <summary>
        /// Cleanup disabled plugins.
        /// </summary>
        public void CleanupPlugins()
        {
            try
            {
                var pluginsDirectory = new DirectoryInfo(this.pluginDirectory);
                foreach (var installed in pluginsDirectory.GetDirectories())
                {
                    var versions = installed.GetDirectories();

                    var sortedVersions = versions.OrderBy(dirInfo =>
                    {
                        var success = Version.TryParse(dirInfo.Name, out var version);
                        if (!success)
                        {
                            Log.Debug("Unparseable version: {0}", dirInfo.Name);
                        }

                        return version;
                    }).ToArray();

                    foreach (var version in sortedVersions)
                    {
                        try
                        {
                            var disabledFile = new FileInfo(Path.Combine(version.FullName, ".disabled"));
                            var definition = JsonConvert.DeserializeObject<PluginDefinition>(
                                File.ReadAllText(Path.Combine(version.FullName, version.Parent.Name + ".json")));

                            if (disabledFile.Exists)
                            {
                                Log.Information("[PLUGINR] Disabled: cleaning up {0} at {1}", installed.Name, version.FullName);
                                try
                                {
                                    version.Delete(true);
                                }
                                catch (Exception ex)
                                {
                                    Log.Error(ex, $"[PLUGINR] Could not clean up {disabledFile.FullName}");
                                }
                            }

                            if (definition.DalamudApiLevel < PluginManager.DalamudApiLevel - 1)
                            {
                                Log.Information("[PLUGINR] Lower API: cleaning up {0} at {1}", installed.Name, version.FullName);
                                try
                                {
                                    version.Delete(true);
                                }
                                catch (Exception ex)
                                {
                                    Log.Error(ex, $"[PLUGINR] Could not clean up {disabledFile.FullName}");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, $"[PLUGINR] Could not clean up {version.FullName}");
                        }

                        if (installed.GetDirectories().Length == 0)
                        {
                            Log.Information("[PLUGINR] Has no versions, cleaning up: {0}", installed.FullName);

                            try
                            {
                                installed.Delete();
                            }
                            catch (Exception ex)
                            {
                                Log.Error(ex, $"[PLUGINR] Could not clean up {installed.FullName}");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[PLUGINR] Plugin cleanup failed.");
            }
        }

        /// <summary>
        /// Plugin update status.
        /// </summary>
        internal class PluginUpdateStatus
        {
            /// <summary>
            /// Gets or sets the plugin internal name.
            /// </summary>
            public string InternalName { get; set; }

            /// <summary>
            /// Gets or sets the plugin name.
            /// </summary>
            public string Name { get; set; }

            /// <summary>
            /// Gets or sets the plugin version.
            /// </summary>
            public string Version { get; set; }

            /// <summary>
            /// Gets or sets a value indicating whether the plugin was updated.
            /// </summary>
            public bool WasUpdated { get; set; }
        }
    }
}
