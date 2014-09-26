﻿/* ****************************************************************************
 *
 * Copyright (c) Microsoft Corporation. 
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
 * copy of the license can be found in the License.html file at the root of this distribution. If 
 * you cannot locate the Apache License, Version 2.0, please send an email to 
 * vspython@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.NodejsTools.Intellisense;
using Microsoft.NodejsTools.ProjectWizard;
using Microsoft.VisualStudioTools;
using Microsoft.VisualStudioTools.Project;
using Microsoft.VisualStudioTools.Project.Automation;
using MSBuild = Microsoft.Build.Evaluation;

namespace Microsoft.NodejsTools.Project {
    class NodejsProjectNode : CommonProjectNode, VsWebSite.VSWebSite, INodePackageModulesCommands {
        private VsProjectAnalyzer _analyzer;
        private readonly HashSet<string> _warningFiles = new HashSet<string>();
        private readonly HashSet<string> _errorFiles = new HashSet<string>();
        private string[] _analysisIgnoredDirs = new string[0];
        internal readonly RequireCompletionCache _requireCompletionCache = new RequireCompletionCache();
        private string _intermediateOutputPath;
        private readonly Dictionary<NodejsProjectImageName, int> _imageIndexFromNameDictionary = new Dictionary<NodejsProjectImageName, int>();


        public NodejsProjectNode(NodejsProjectPackage package)
            : base(package, Utilities.GetImageList(typeof(NodejsProjectNode).Assembly.GetManifestResourceStream("Microsoft.NodejsTools.Resources.Icons.NodejsImageList.bmp"))) {

            Type projectNodePropsType = typeof(NodejsProjectNodeProperties);
            AddCATIDMapping(projectNodePropsType, projectNodePropsType.GUID);
            InitNodejsProjectImages();
        }

        public VsProjectAnalyzer Analyzer {
            get {
                return _analyzer;
            }
        }

        private static string[] _excludedAvailableItems = new[] { 
            "ApplicationDefinition", 
            "Page",
            "Resource",
            "SplashScreen",
            "DesignData",
            "DesignDataWithDesignTimeCreatableTypes",
            "EntityDeploy",
            "CodeAnalysisDictionary", 
            "XamlAppDef"
        };

        public override IEnumerable<string> GetAvailableItemNames() {
            // Remove a couple of available item names which show up from imports we
            // can't control out of Microsoft.Common.targets.
            return base.GetAvailableItemNames().Except(_excludedAvailableItems);
        }

        public Dictionary<NodejsProjectImageName, int> ImageIndexFromNameDictionary {
            get { return _imageIndexFromNameDictionary; }
        }

        private void InitNodejsProjectImages() {
            // HACK: https://nodejstools.codeplex.com/workitem/1268

            // Project file images
            AddProjectImage(NodejsProjectImageName.TypeScriptProjectFile, "Microsoft.VisualStudioTools.Resources.Icons.TSProject_SolutionExplorerNode.png");

            // Dependency images
            AddProjectImage(NodejsProjectImageName.Dependency, "Microsoft.VisualStudioTools.Resources.Icons.NodeJSPackage_16x.png");
            AddProjectImage(NodejsProjectImageName.DependencyNotListed, "Microsoft.VisualStudioTools.Resources.Icons.NodeJSPackageMissing_16x.png");
            AddProjectImage(NodejsProjectImageName.DependencyMissing, "Microsoft.VisualStudioTools.Resources.Icons.PackageWarning_16x.png");
        }

        private void AddProjectImage(NodejsProjectImageName name, string resourceId) {
            var images = ImageHandler.ImageList.Images;
            ImageIndexFromNameDictionary.Add(name, images.Count);
            images.Add(Image.FromStream(typeof(NodejsProjectNode).Assembly.GetManifestResourceStream(resourceId)));
        }

        public override Guid SharedCommandGuid {
            get {
                return Guids.NodejsCmdSet;
            }
        }

        public override int ImageIndex {
            get {
                if (string.Equals(GetProjectProperty(NodejsConstants.EnableTypeScript), "true", StringComparison.OrdinalIgnoreCase)) {
                    return ImageIndexFromNameDictionary[NodejsProjectImageName.TypeScriptProjectFile];
                }
                return base.ImageIndex;
            }
        }

        protected override void FinishProjectCreation(string sourceFolder, string destFolder) {
            foreach (MSBuild.ProjectItem item in this.BuildProject.Items) {
                if (String.Equals(Path.GetExtension(item.EvaluatedInclude), NodejsConstants.TypeScriptExtension, StringComparison.OrdinalIgnoreCase)) {

                    // Create the 'typings' folder
                    var typingsFolder = Path.Combine(ProjectHome, "Scripts", "typings");
                    if (!Directory.Exists(typingsFolder)) {
                        Directory.CreateDirectory(typingsFolder);
                    }

                    // Deploy node.d.ts
                    var nodeTypingsFolder = Path.Combine(typingsFolder, "node");
                    if (!Directory.Exists(Path.Combine(nodeTypingsFolder))) {
                        Directory.CreateDirectory(nodeTypingsFolder);
                    }

                    var nodeFolder = ((OAProject)this.GetAutomationObject()).ProjectItems
                        .AddFolder("Scripts").ProjectItems
                        .AddFolder("typings").ProjectItems
                        .AddFolder("node");

                    nodeFolder.ProjectItems.AddFromFileCopy(
                        Path.Combine(
                            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
                            "Scripts",
                            "typings",
                            "node",
                            "node.d.ts"
                        )
                    );
                    break;
                }
            }

            base.FinishProjectCreation(sourceFolder, destFolder);
        }

        protected override void AddNewFileNodeToHierarchy(HierarchyNode parentNode, string fileName) {
            base.AddNewFileNodeToHierarchy(parentNode, fileName);

            if (String.Equals(Path.GetExtension(fileName), NodejsConstants.TypeScriptExtension, StringComparison.OrdinalIgnoreCase) &&
                !String.Equals(GetProjectProperty(NodejsConstants.EnableTypeScript), "true", StringComparison.OrdinalIgnoreCase)) {
                // enable type script on the project automatically...
                SetProjectProperty(NodejsConstants.EnableTypeScript, "true");
                SetProjectProperty(NodejsConstants.TypeScriptSourceMap, "true");
                if (String.IsNullOrWhiteSpace(GetProjectProperty(NodejsConstants.TypeScriptModuleKind))) {
                    SetProjectProperty(NodejsConstants.TypeScriptModuleKind, NodejsConstants.CommonJSModuleKind);
                }
            }
        }

        internal static bool IsNodejsFile(string strFileName) {
            var ext = Path.GetExtension(strFileName);

            return String.Equals(ext, NodejsConstants.JavaScriptExtension, StringComparison.OrdinalIgnoreCase);
        }

        internal override string GetItemType(string filename) {
            if (string.Equals(Path.GetExtension(filename), NodejsConstants.TypeScriptExtension, StringComparison.OrdinalIgnoreCase)) {
                return NodejsConstants.TypeScriptCompileItemType;
            }
            return base.GetItemType(filename);
        }

        protected override bool DisableCmdInCurrentMode(Guid commandGroup, uint command) {
            if (commandGroup == Guids.OfficeToolsBootstrapperCmdSet) {
                // Convert to ... commands from Office Tools don't make sense and aren't supported 
                // on our project type
                const int AddOfficeAppProject = 0x0001;
                const int AddSharePointAppProject = 0x0002;

                if (command == AddOfficeAppProject || command == AddSharePointAppProject) {
                    return true;
                }
            }

            return base.DisableCmdInCurrentMode(commandGroup, command);
        }

        public override string[] CodeFileExtensions {
            get {
                return new[] { NodejsConstants.JavaScriptExtension };
            }
        }

        protected internal override FolderNode CreateFolderNode(ProjectElement element) {
            return new CommonFolderNode(this, element);
        }

        public override CommonFileNode CreateCodeFileNode(ProjectElement item) {
            var res = new NodejsFileNode(this, item);
            return res;
        }

        public override CommonFileNode CreateNonCodeFileNode(ProjectElement item) {
            string fileName = item.Url;
            if (!String.IsNullOrWhiteSpace(fileName)
                && Path.GetExtension(fileName).Equals(NodejsConstants.TypeScriptExtension, StringComparison.OrdinalIgnoreCase)) {
                return new NodejsTypeScriptFileNode(this, item);
            }
            if (Path.GetFileName(fileName).Equals(NodejsConstants.PackageJsonFile, StringComparison.OrdinalIgnoreCase)) {
                return new PackageJsonFileNode(this, item);
            }

            return base.CreateNonCodeFileNode(item);
        }

        public override string GetProjectName() {
            return "NodeProject";
        }

        public override Type GetProjectFactoryType() {
            return typeof(BaseNodeProjectFactory);
        }

        public override Type GetEditorFactoryType() {
            // Not presently used
            throw new NotImplementedException();
        }

        public override string GetFormatList() {
            return NodejsConstants.ProjectFileFilter;
        }

        protected override Guid[] GetConfigurationDependentPropertyPages() {
            var res = base.GetConfigurationDependentPropertyPages();

            var enableTs = GetProjectProperty(NodejsConstants.EnableTypeScript, false);
            bool fEnableTs;
            if (enableTs != null && Boolean.TryParse(enableTs, out fEnableTs) && fEnableTs) {
                var typeScriptPages = GetProjectProperty(NodejsConstants.TypeScriptCfgProperty);
                if (typeScriptPages != null) {
                    foreach (var strGuid in typeScriptPages.Split(';')) {
                        Guid guid;
                        if (Guid.TryParse(strGuid, out guid)) {
                            res = res.Append(guid);
                        }
                    }
                }
            }

            return res;
        }

        public override Type GetGeneralPropertyPageType() {
            return typeof(NodejsGeneralPropertyPage);
        }

        public override Type GetLibraryManagerType() {
            return typeof(NodejsLibraryManager);
        }

        public override IProjectLauncher GetLauncher() {
            return new NodejsProjectLauncher(this);
        }

        protected override NodeProperties CreatePropertiesObject() {
            return new NodejsProjectNodeProperties(this);
        }

        protected override Stream ProjectIconsImageStripStream {
            get {
                return typeof(ProjectNode).Assembly.GetManifestResourceStream("Microsoft.VisualStudioTools.Resources.Icons.SharedProjectImageList.bmp");
            }
        }

        public override bool IsCodeFile(string fileName) {
            return Path.GetExtension(fileName).Equals(NodejsConstants.JavaScriptExtension, StringComparison.OrdinalIgnoreCase);
        }

        public override int InitializeForOuter(string filename, string location, string name, uint flags, ref Guid iid, out IntPtr projectPointer, out int canceled) {
            NodejsPackage.Instance.IntellisenseOptionsPage.AnalysisLevelChanged += IntellisenseOptionsPageAnalysisLevelChanged;
            NodejsPackage.Instance.IntellisenseOptionsPage.AnalysisLogMaximumChanged += AnalysisLogMaximumChanged;
            NodejsPackage.Instance.IntellisenseOptionsPage.SaveToDiskChanged += IntellisenseOptionsPageSaveToDiskChanged;

            return base.InitializeForOuter(filename, location, name, flags, ref iid, out projectPointer, out canceled);
        }

        protected override void Reload() {
            using (new DebugTimer("Project Load")) {
                _intermediateOutputPath = Path.Combine(ProjectHome, GetProjectProperty("BaseIntermediateOutputPath"));

                if (_analyzer != null && _analyzer.RemoveUser()) {
                    _analyzer.Dispose();
                }
                _analyzer = new VsProjectAnalyzer(ProjectHome);
                _analyzer.MaxLogLength = NodejsPackage.Instance.IntellisenseOptionsPage.AnalysisLogMax;
                LogAnalysisLevel();

                base.Reload();

                SyncFileSystem();

                NodejsPackage.Instance.CheckSurveyNews(false);
                ModulesNode.ReloadHierarchySafe();

                // scan for files which were loaded from cached analysis but no longer
                // exist and remove them.
                _analyzer.ReloadComplete();

                var ignoredPaths = GetProjectProperty(NodejsConstants.AnalysisIgnoredDirectories);

                if (!string.IsNullOrWhiteSpace(ignoredPaths)) {
                    _analysisIgnoredDirs = ignoredPaths.Split(';').Select(x => '\\' + x + '\\').ToArray();
                } else {
                    _analysisIgnoredDirs = new string[0];
                }
            }
        }

        private void Reanalyze(HierarchyNode node, VsProjectAnalyzer newAnalyzer) {
            if (node != null) {
                for (var child = node.FirstChild; child != null; child = child.NextSibling) {
                    if (child is PackageJsonFileNode) {
                        ((PackageJsonFileNode)child).AnalyzePackageJson(newAnalyzer);
                    } else if (child is NodejsFileNode) {
                        if (((NodejsFileNode)child).ShouldAnalyze) {
                            newAnalyzer.AnalyzeFile(child.Url, !child.IsNonMemberItem);
                        }
                    }

                    Reanalyze(child, newAnalyzer);
                }
            }
        }

        private void LogAnalysisLevel() {
            var analyzer = _analyzer;
            if (analyzer != null) {
                NodejsPackage.Instance.Logger.LogEvent(Logging.NodejsToolsLogEvent.AnalysisLevel, (int)analyzer.AnalysisLevel);
            }
        }

        /*
         * Needed if we switch to per project Analysis levels
        internal NodejsTools.Options.AnalysisLevel AnalysisLevel(){
            var analyzer = _analyzer;
            if (_analyzer != null) {
                return _analyzer.AnalysisLevel;
            }
            return NodejsTools.Options.AnalysisLevel.None;
        }
        */
        private void IntellisenseOptionsPageAnalysisLevelChanged(object sender, EventArgs e) {
            var analyzer = new VsProjectAnalyzer(ProjectHome);
            Reanalyze(this, analyzer);
            if (_analyzer != null) {
                analyzer.SwitchAnalyzers(_analyzer);
                if (_analyzer.RemoveUser()) {
                    _analyzer.Dispose();
                }
            }
            _analyzer = analyzer;
            _analyzer.MaxLogLength = NodejsPackage.Instance.IntellisenseOptionsPage.AnalysisLogMax;
            LogAnalysisLevel();
        }

        private void AnalysisLogMaximumChanged(object sender, EventArgs e) {
            if (_analyzer != null) {
                _analyzer.MaxLogLength = NodejsPackage.Instance.IntellisenseOptionsPage.AnalysisLogMax;
            }
        }

        private void IntellisenseOptionsPageSaveToDiskChanged(object sender, EventArgs e) {
            if (_analyzer != null) {
                _analyzer.SaveToDisk = NodejsPackage.Instance.IntellisenseOptionsPage.SaveToDisk;
            }
        }

        protected override void RaiseProjectPropertyChanged(string propertyName, string oldValue, string newValue) {
            base.RaiseProjectPropertyChanged(propertyName, oldValue, newValue);

            var propPage = GeneralPropertyPageControl;
            if (propPage != null) {
                switch (propertyName) {
                    case NodejsConstants.Environment:
                        propPage.Environment = newValue;
                        break;
                    case NodejsConstants.DebuggerPort:
                        propPage.DebuggerPort = newValue;
                        break;
                    case NodejsConstants.NodejsPort:
                        propPage.NodejsPort = newValue;
                        break;
                    case NodejsConstants.NodeExePath:
                        propPage.NodeExePath = newValue;
                        break;
                    case NodejsConstants.NodeExeArguments:
                        propPage.NodeExeArguments = newValue;
                        break;
                    case NodejsConstants.ScriptArguments:
                        propPage.ScriptArguments = newValue;
                        break;
                    case NodejsConstants.LaunchUrl:
                        propPage.LaunchUrl = newValue;
                        break;
                    case NodejsConstants.StartWebBrowser:
                        bool value;
                        if (Boolean.TryParse(newValue, out value)) {
                            propPage.StartWebBrowser = value;
                        }
                        break;
                    case CommonConstants.WorkingDirectory:
                        propPage.WorkingDirectory = newValue;
                        break;
                    default:
                        if (propPage != null) {
                            PropertyPage.IsDirty = true;
                        }
                        break;
                }
            }
        }

        private NodejsGeneralPropertyPageControl GeneralPropertyPageControl {
            get {
                if (PropertyPage != null && PropertyPage.Control != null) {
                    return (NodejsGeneralPropertyPageControl)PropertyPage.Control;
                }

                return null;
            }
        }

        private static void AddFolderForFile(Dictionary<FileNode, List<CommonFolderNode>> directoryPackages, FileNode rootFile, CommonFolderNode folderChild) {
            List<CommonFolderNode> folders;
            if (!directoryPackages.TryGetValue(rootFile, out folders)) {
                directoryPackages[rootFile] = folders = new List<CommonFolderNode>();
            }
            folders.Add(folderChild);
        }

        protected override bool IncludeNonMemberItemInProject(HierarchyNode node) {
            var fileNode = node as NodejsFileNode;
            if (fileNode != null) {
                return IncludeNodejsFile(fileNode);
            }
            return false;
        }

        internal bool IncludeNodejsFile(NodejsFileNode fileNode) {
            var url = fileNode.Url;
            if (CommonUtils.IsSubpathOf(_intermediateOutputPath, fileNode.Url)) {
                return false;
            }
            foreach (var path in _analysisIgnoredDirs) {
                if (url.IndexOf(path, 0, StringComparison.OrdinalIgnoreCase) != -1) {
                    return false;
                }
            }
            return true;
        }

        internal override object Object {
            get {
                return this;
            }
        }

        protected override ReferenceContainerNode CreateReferenceContainerNode() {
            return null;
        }

        public NodeModulesNode ModulesNode { get; private set; }



        protected internal override void ProcessReferences() {
            base.ProcessReferences();

            if (null == ModulesNode) {
                ModulesNode = new NodeModulesNode(this);
                AddChild(ModulesNode);
            }
        }

        #region VSWebSite Members

        // This interface is just implemented so we don't get normal profiling which
        // doesn't work with our projects anyway.

        public EnvDTE.ProjectItem AddFromTemplate(string bstrRelFolderUrl, string bstrWizardName, string bstrLanguage, string bstrItemName, bool bUseCodeSeparation, string bstrMasterPage, string bstrDocType) {
            throw new NotImplementedException();
        }

        public VsWebSite.CodeFolders CodeFolders {
            get { throw new NotImplementedException(); }
        }

        public EnvDTE.DTE DTE {
            get { return Project.DTE; }
        }

        public string EnsureServerRunning() {
            throw new NotImplementedException();
        }

        public string GetUniqueFilename(string bstrFolder, string bstrRoot, string bstrDesiredExt) {
            throw new NotImplementedException();
        }

        public bool PreCompileWeb(string bstrCompilePath, bool bUpdateable) {
            throw new NotImplementedException();
        }

        public EnvDTE.Project Project {
            get { return (OAProject)GetAutomationObject(); }
        }

        public VsWebSite.AssemblyReferences References {
            get { throw new NotImplementedException(); }
        }

        public void Refresh() {
        }

        public string TemplatePath {
            get { throw new NotImplementedException(); }
        }

        public string URL {
            get { throw new NotImplementedException(); }
        }

        public string UserTemplatePath {
            get { throw new NotImplementedException(); }
        }

        public VsWebSite.VSWebSiteEvents VSWebSiteEvents {
            get { throw new NotImplementedException(); }
        }

        public void WaitUntilReady() {
        }

        public VsWebSite.WebReferences WebReferences {
            get { throw new NotImplementedException(); }
        }

        public VsWebSite.WebServices WebServices {
            get { throw new NotImplementedException(); }
        }

        #endregion

        Task INodePackageModulesCommands.InstallMissingModulesAsync() {
            //Fire off the command to update the missing modules
            //  through NPM
            return ModulesNode.InstallMissingModules();
        }

        private void HookErrorsAndWarnings(VsProjectAnalyzer res) {
            res.ErrorAdded += OnErrorAdded;
            res.ErrorRemoved += OnErrorRemoved;
            res.WarningAdded += OnWarningAdded;
            res.WarningRemoved += OnWarningRemoved;
        }

        private void UnHookErrorsAndWarnings(VsProjectAnalyzer res) {
            res.ErrorAdded -= OnErrorAdded;
            res.ErrorRemoved -= OnErrorRemoved;
            res.WarningAdded -= OnWarningAdded;
            res.WarningRemoved -= OnWarningRemoved;
        }

        private void OnErrorAdded(object sender, FileEventArgs args) {
            if (_diskNodes.ContainsKey(args.Filename)) {
                _errorFiles.Add(args.Filename);
            }
        }

        private void OnErrorRemoved(object sender, FileEventArgs args) {
            _errorFiles.Remove(args.Filename);
        }

        private void OnWarningAdded(object sender, FileEventArgs args) {
            if (_diskNodes.ContainsKey(args.Filename)) {
                _warningFiles.Add(args.Filename);
            }
        }

        private void OnWarningRemoved(object sender, FileEventArgs args) {
            _warningFiles.Remove(args.Filename);
        }

        /// <summary>
        /// File names within the project which contain errors.
        /// </summary>
        public HashSet<string> ErrorFiles {
            get {
                return _errorFiles;
            }
        }

        /// <summary>
        /// File names within the project which contain warnings.
        /// </summary>
        public HashSet<string> WarningFiles {
            get {
                return _warningFiles;
            }
        }

        internal struct LongPathInfo {
            public string FullPath;
            public string RelativePath;
            public bool IsDirectory;
        }

        private static readonly char[] _pathSeparators = { '\\', '/' };
        private bool _isCheckingForLongPaths;

        public async Task CheckForLongPaths() {
            if (_isCheckingForLongPaths || !NodejsPackage.Instance.GeneralOptionsPage.CheckForLongPaths) {
                return;
            }
            try {
                _isCheckingForLongPaths = true;
                TaskDialogButton dedupButton, ignoreButton, disableButton;
                var taskDialog = new TaskDialog(NodejsPackage.Instance) {
                    AllowCancellation = true,
                    EnableHyperlinks = true,
                    Title = SR.GetString(SR.LongPathWarningTitle),
                    MainIcon = TaskDialogIcon.Warning,
                    Content = SR.GetString(SR.LongPathWarningText),
                    CollapsedControlText = SR.GetString(SR.LongPathShowPathsExceedingTheLimit),
                    ExpandedControlText = SR.GetString(SR.LongPathHidePathsExceedingTheLimit),
                    Buttons = {
                        (dedupButton = new TaskDialogButton(SR.GetString(SR.LongPathNpmDedup), SR.GetString(SR.LongPathNpmDedupDetail))),
                        (ignoreButton = new TaskDialogButton(SR.GetString(SR.LongPathDoNothingButWarnNextTime))),
                        (disableButton = new TaskDialogButton(SR.GetString(SR.LongPathDoNothingAndDoNotWarnAgain), SR.GetString(SR.LongPathDoNothingAndDoNotWarnAgainDetail)))
                    },
                    FooterIcon = TaskDialogIcon.Information,
                    Footer = SR.GetString(SR.LongPathFooter)
                };

                taskDialog.HyperlinkClicked += (sender, e) => {
                    switch (e.Url) {
                        case "#msdn":
                            Process.Start("http://go.microsoft.com/fwlink/?LinkId=454508");
                            break;
                        case "#uservoice":
                            Process.Start("http://go.microsoft.com/fwlink/?LinkID=456509");
                            break;
                        case "#help":
                            Process.Start("http://go.microsoft.com/fwlink/?LinkId=456511");
                            break;
                        default:
                            System.Windows.Clipboard.SetText(e.Url);
                            break;
                    }
                };

            recheck:

                var longPaths = await Task.Factory.StartNew(() =>
                    GetLongSubPaths(ProjectHome)
                    .Concat(GetLongSubPaths(_intermediateOutputPath))
                    .Select(lpi => string.Format("• {1}\u00A0<a href=\"{0}\">{2}</a>", lpi.FullPath, lpi.RelativePath, SR.GetString(SR.LongPathClickToCopy)))
                    .ToArray());
                if (longPaths.Length == 0) {
                    return;
                }
                taskDialog.ExpandedInformation = string.Join("\r\n", longPaths);

                var button = taskDialog.ShowModal();
                if (button == dedupButton) {
                    var repl = NodejsPackage.Instance.OpenReplWindow(focus: false);
                    await repl.ExecuteCommand(".npm dedup").HandleAllExceptions(SR.ProductName);

                    taskDialog.Content += "\r\n\r\n" + SR.GetString(SR.LongPathNpmDedupDidNotHelp);
                    taskDialog.Buttons.Remove(dedupButton);
                    goto recheck;
                } else if (button == disableButton) {
                    NodejsPackage.Instance.GeneralOptionsPage.CheckForLongPaths = false;
                }
            } finally {
                _isCheckingForLongPaths = false;
            }
        }

        internal static IEnumerable<LongPathInfo> GetLongSubPaths(string basePath, string path = "") {
            const int MaxFilePathLength = 260 - 1; // account for terminating NULL
            const int MaxDirectoryPathLength = 248 - 1;

            basePath = CommonUtils.EnsureEndSeparator(basePath);

            WIN32_FIND_DATA wfd;
            IntPtr hFind = NativeMethods.FindFirstFile(basePath + path + "\\*", out wfd);
            if (hFind == NativeMethods.INVALID_HANDLE_VALUE) {
                yield break;
            }

            try {
                do {
                    if (wfd.cFileName == "." || wfd.cFileName == "..") {
                        continue;
                    }

                    bool isDirectory = (wfd.dwFileAttributes & NativeMethods.FILE_ATTRIBUTE_DIRECTORY) != 0;

                    string childPath = path;
                    if (childPath != "") {
                        childPath += "\\";
                    }
                    childPath += wfd.cFileName;

                    string fullChildPath = basePath + childPath;
                    bool isTooLong;
                    try {
                        isTooLong = Path.GetFullPath(fullChildPath).Length > (isDirectory ? MaxDirectoryPathLength : MaxFilePathLength);
                    } catch (PathTooLongException) {
                        isTooLong = true;
                    } catch (Exception) {
                        continue;
                    }

                    if (isTooLong) {
                        yield return new LongPathInfo { FullPath = fullChildPath, RelativePath = childPath, IsDirectory = isDirectory };
                    } else if (isDirectory) {
                        foreach (var item in GetLongSubPaths(basePath, childPath)) {
                            yield return item;
                        }
                    }
                } while (NativeMethods.FindNextFile(hFind, out wfd));
            } finally {
                NativeMethods.FindClose(hFind);
            }
        }

        protected override void Dispose(bool disposing) {
            if (disposing) {
                if (_analyzer != null) {
                    UnHookErrorsAndWarnings(_analyzer);
                    if (WarningFiles.Count > 0 || ErrorFiles.Count > 0) {
                        foreach (var file in WarningFiles.Concat(ErrorFiles)) {
                            var node = FindNodeByFullPath(file) as NodejsFileNode;
                            if (node != null) {
                                //_analyzer.RemoveErrors(node.GetAnalysis(), suppressUpdate: false);
                            }
                        }
                    }

                    if (_analyzer.RemoveUser()) {
                        _analyzer.Dispose();
                    }

                    _analyzer = null;
                }

                NodejsPackage.Instance.IntellisenseOptionsPage.SaveToDiskChanged -= IntellisenseOptionsPageSaveToDiskChanged;
                NodejsPackage.Instance.IntellisenseOptionsPage.AnalysisLevelChanged -= IntellisenseOptionsPageAnalysisLevelChanged;
                NodejsPackage.Instance.IntellisenseOptionsPage.AnalysisLogMaximumChanged -= AnalysisLogMaximumChanged;
            }
            base.Dispose(disposing);
        }

        internal override async void BuildAsync(uint vsopts, string config, VisualStudio.Shell.Interop.IVsOutputWindowPane output, string target, Action<MSBuildResult, string> uiThreadCallback) {
            try {
                await CheckForLongPaths();
            } catch (Exception) {
                uiThreadCallback(MSBuildResult.Failed, target);
                return;
            }

            // BuildAsync can throw on the sync path before invoking the callback. If it does, we must still invoke the callback here,
            // because by this time there's no other way to propagate the error to the caller.
            try {
                base.BuildAsync(vsopts, config, output, target, uiThreadCallback);
            } catch (Exception) {
                uiThreadCallback(MSBuildResult.Failed, target);
            }
        }
    }
}
