using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.ComponentModel.Design;
using System.IO;
using Task = System.Threading.Tasks.Task;

namespace LinuxRemoteDebugger
{
    /// <summary>
    /// Command handler
    /// </summary>
    internal sealed class LaunchDebuggerCommand
    {
        /// <summary>
        /// Command ID.
        /// </summary>
        public const int CommandId = 0x0100;

        /// <summary>
        /// Command menu group (command set GUID).
        /// </summary>
        public static readonly Guid CommandSet = new Guid("eebafa58-3bf2-4751-a24b-4363f46c8d0e");

        /// <summary>
        /// VS Package that provides this command, not null.
        /// </summary>
        private readonly AsyncPackage _package;

        /// <summary>
        /// Initializes a new instance of the <see cref="LaunchDebuggerCommand"/> class.
        /// Adds our command handlers for menu (commands must exist in the command table file)
        /// </summary>
        /// <param name="package">Owner package, not null.</param>
        /// <param name="commandService">Command service to add command to, not null.</param>
        private LaunchDebuggerCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
            commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));

            var menuCommandID = new CommandID(CommandSet, CommandId);
            var menuItem = new MenuCommand(this.Execute, menuCommandID);
            commandService.AddCommand(menuItem);
        }

        /// <summary>
        /// Gets the instance of the command.
        /// </summary>
        public static LaunchDebuggerCommand Instance { get; private set; }

        /// <summary>
        /// Initializes the singleton instance of the command.
        /// </summary>
        /// <param name="package">Owner package, not null.</param>
        public static async Task InitializeAsync(AsyncPackage package)
        {
            // Switch to the main thread - the call to AddCommand in LaunchDebuggerCommand's constructor requires
            // the UI thread.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            OleMenuCommandService commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            Instance = new LaunchDebuggerCommand(package, commandService);
        }

        /// <summary>
        /// This function is the callback used to execute the command when the menu item is clicked.
        /// See the constructor to see how the menu item is associated with this function using
        /// OleMenuCommandService service and MenuCommand class.
        /// </summary>
        /// <param name="sender">Event sender.</param>
        /// <param name="e">Event args.</param>
        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            const string title = "LaunchDebuggerCommand";
            const string launchFileName = "launch.json";
            const string prelaunchFileName = "prelaunch.bat";

            var dte = (DTE2)Package.GetGlobalService(typeof(SDTE));
            var monitorSelection = Package.GetGlobalService(typeof(SVsShellMonitorSelection)); 

            // get project
            var activeProjects = (object[])dte.ActiveSolutionProjects;
            var project = activeProjects[0] as Project;

            // get solution
            var solution = dte.Solution as Solution2;

            // get file paths
            ProjectLaunchFilePath = null; // reset file path
            var paths = new string[] { solution.FullName, project.FullName };
            foreach (var path in paths)
            {
                var directory = Path.GetDirectoryName(path);
                var fullPath = Path.Combine(directory, launchFileName);
                if (File.Exists(fullPath))
                {
                    ProjectLaunchFilePath = fullPath;

                    var prelaunchFilePath = Path.Combine(directory, prelaunchFileName);
                    if (File.Exists(prelaunchFilePath))
                    {
                        ProjectPrelaunchFilePath = prelaunchFilePath;
                    }

                    break;
                }
            }

            if (ProjectLaunchFilePath == null)
            {
                var msg = $"{launchFileName} not found.";
                VsShellUtilities.ShowMessageBox(
                    _package,
                    msg,
                    title,
                    OLEMSGICON.OLEMSGICON_CRITICAL,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                return;
            }

            if (BuildEvents == null)
            {
                var events = dte.Events as Events2;
                BuildEvents = events.BuildEvents;
                BuildEvents.OnBuildProjConfigDone += this.BuildEvents_OnBuildProjConfigDone;
                BuildEvents.OnBuildDone += this.BuildEvents_OnBuildDone;
            }

            dte.SuppressUI = false;
            
            BuildProject = project.UniqueName;

            var configurationName = project.ConfigurationManager.ActiveConfiguration.ConfigurationName;
            
            var solutionBuild = dte.Solution.SolutionBuild as SolutionBuild2;
            solutionBuild.BuildProject(configurationName, project.UniqueName);
        }

        #region Properties

        public BuildEvents BuildEvents { get; private set; }
        public bool RemoteDebugLaunchOnDone { get; private set; }
        public string BuildProject { get; private set; }
        public string ProjectLaunchFilePath { get; private set; }
        public string ProjectPrelaunchFilePath { get; private set; }

        #endregion

        #region Event Handlers

        private void BuildEvents_OnBuildProjConfigDone(string project, string projectConfig, string platform, string solutionConfig, bool success)
        {
            RemoteDebugLaunchOnDone = project == BuildProject && success;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100:Avoid async void methods", Justification = "Event Handler")]
        private async void BuildEvents_OnBuildDone(vsBuildScope scope, vsBuildAction action)
        {
            if (RemoteDebugLaunchOnDone)
            {
                var dte = (DTE2)Package.GetGlobalService(typeof(SDTE));

                if (!string.IsNullOrWhiteSpace(ProjectPrelaunchFilePath))
                {
                    // run prelaunch script
                    await RunProcessAsync(ProjectPrelaunchFilePath);
                }

                dte.ExecuteCommand("DebugAdapterHost.Launch", $"/LaunchJson:\"{ProjectLaunchFilePath}\" /EngineGuid:541B8A8A-6081-4506-9F0A-1CE771DEBC04");
            }

            RemoteDebugLaunchOnDone = false;
            BuildProject = null;
        }

        #endregion

        private Task RunProcessAsync(string fileName)
        {
            return Task.Run(() =>
            {
                var startinfo = new System.Diagnostics.ProcessStartInfo()
                {
                    FileName = fileName,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                };
                using (var process = System.Diagnostics.Process.Start(startinfo))
                {
                    process.WaitForExit();
                }
            });
        }
    }
}
