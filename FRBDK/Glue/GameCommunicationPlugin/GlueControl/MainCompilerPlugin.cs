﻿using FlatRedBall.Glue.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FlatRedBall.Glue.Plugins.Interfaces;
using System.ComponentModel.Composition;
using GameCommunicationPlugin.GlueControl.ViewModels;
using FlatRedBall.Glue.Plugins.ExportedImplementations;
using FlatRedBall.Glue.Managers;
using System.Windows;
using GameCommunicationPlugin.GlueControl.CodeGeneration;
using System.Net.Sockets;
using GameCommunicationPlugin.GlueControl.Managers;
using FlatRedBall.Glue.Controls;
using System.ComponentModel;
using FlatRedBall.Glue.IO;
using Newtonsoft.Json;
using CompilerLibrary.Models;
using FlatRedBall.Glue.SaveClasses;
using FlatRedBall.IO;
using OfficialPluginsCore.Compiler.ViewModels;
using OfficialPluginsCore.Compiler.Managers;
using System.Diagnostics;
using System.Timers;
using Glue;
using OfficialPluginsCore.Compiler.CommandReceiving;
using FlatRedBall.Glue.Elements;
using GameCommunicationPlugin.GlueControl.Dtos;
using GameCommunicationPlugin.GlueControl.CommandSending;
using System.Runtime.InteropServices;
using OfficialPlugins.GameHost.Views;
using GameCommunicationPlugin.GlueControl.Views;
using FlatRedBall.Glue.FormHelpers;
using GlueFormsCore.FormHelpers;
using System.Windows.Media.Imaging;
using FlatRedBall.Glue.ViewModels;
using GameCommunicationPlugin.GlueControl.CodeGeneration.GlueCalls;
using Newtonsoft.Json.Linq;
using CompilerLibrary.ViewModels;
using FlatRedBall.Glue.Plugins.ExportedInterfaces.CommandInterfaces;
using System.Security.Permissions;
using Microsoft.Xna.Framework.Graphics;
using GeneralResponse = ToolsUtilities.GeneralResponse;

namespace GameCommunicationPlugin.GlueControl
{
    [Export(typeof(PluginBase))]
    public class MainCompilerPlugin : PluginBase
    {
        #region Fields/Properties


        public CompilerViewModel CompilerViewModel { get; private set; }

        public GlueViewSettingsViewModel GlueViewSettingsViewModel { get; private set; }
        

        public static CompilerViewModel MainViewModel { get; private set; }

        public static MainCompilerPlugin Self { get; private set; }

        PluginTab glueViewSettingsTab;
        GlueViewSettings glueViewSettingsView;

        Game1GlueControlGenerator game1GlueControlGenerator;

        public override string FriendlyName => "Glue Compiler";

        public override Version Version
        {
            get
            {
                // 0.4 introduces:
                // - multicore building
                // - Removed warnings and information when building - now we just show start, end, and errors
                // - If an error occurs, a popup appears telling the user that the game crashed, and to open Visual Studio
                // 0.5
                // - Support for running content-only builds
                // 0.6
                // - Added VS 2017 support
                // 0.7
                // - Added a list of MSBuild locations
                // 1.0
                // - This was added long ago, but might as well mark it 1.0
                return new Version(1, 0);
            }
        }

        FilePath JsonSettingsFilePath => GlueState.Self.ProjectSpecificSettingsFolder + "CompilerSettings.json";
        

        bool ignoreViewModelChanges = false;

        Timer dragDropTimer;

        System.Threading.SemaphoreSlim getCommandsSemaphore = new System.Threading.SemaphoreSlim(1, 1);

        #endregion

        #region Startup

        public override void StartUp()
        {
            Self = this;
            _refreshManager = new RefreshManager(ReactToPluginEventWithReturn, ReactToPluginEvent);
            _refreshManager.InitializeEvents(
                (value) => PluginManager.CallPluginMethod("Compiler Plugin", "HandleOutput", value), 
                (value) => this.ReactToPluginEvent("Compiler_Output_Error", value));

            _dragDropManagerGameWindow = new DragDropManagerGameWindow(_refreshManager);
            _variableSendingManager = new VariableSendingManager(_refreshManager);
            _commandReceiver = new CommandReceiver(_refreshManager, _variableSendingManager);

            CreateBuildControl();

            CreateToolbar();

            Output.Initialize((value) => PluginManager.CallPluginMethod("Compiler Plugin", "HandleOutput", value));


            game1GlueControlGenerator = new Game1GlueControlGenerator();
            this.RegisterCodeGenerator(game1GlueControlGenerator);

            this.RegisterCodeGenerator(new CompilerPluginElementCodeGenerator());


            ToolbarEntityViewModelManager.CompilerViewModel = CompilerViewModel;
            ToolbarEntityViewModelManager.ReactToPluginEventWithReturn = ReactToPluginEventWithReturn;
            ToolbarEntityViewModelManager.SaveCompilerSettingsModel = SaveCompilerSettingsModel;


            CreateGameTab();

            CreateProfileTab();

            // do this after creating the compiler, view model, and control
            AssignEvents();


        }

        private void AssignEvents()
        {
            var manager = new FileChangeManager((value) => ReactToPluginEvent("Compiler_Output_Standard", value), CompilerViewModel, _refreshManager);
            
            this.ReactToLoadedGlux += HandleGluxLoaded;
            this.ReactToUnloadedGlux += HandleGluxUnloaded;
            
            this.ReactToNewFileHandler += _refreshManager.HandleNewFile;
            this.ReactToFileChange += manager.HandleFileChanged;
            this.ReactToCodeFileChange += _refreshManager.HandleFileChanged;
            this.ReactToElementRenamed += ToolbarController.Self.HandleScreenRenamed;

            this.ReactToChangedPropertyHandler += HandlePropertyChanged;

            this.NewEntityCreated += _refreshManager.HandleNewEntityCreated;
            this.NewScreenCreated += (newScreen) =>
            {
                GlueCommands.Self.DoOnUiThread(() => ToolbarController.Self.HandleNewScreenCreated(newScreen));
                _refreshManager.HandleNewScreenCreated();
                return Task.CompletedTask;
            };
            this.ReactToScreenRemoved += ToolbarController.Self.HandleScreenRemoved;
            // todo - handle startup changed...

            this.ReactToNewObjectListAsync += _refreshManager.HandleNewObjectList;

            this.ReactToObjectRemoved += async (owner, nos) =>
                await _refreshManager.HandleObjectRemoved(owner, nos);
            this.ReactToObjectListRemoved += async (ownerList, list) =>
                await _refreshManager.HandleObjectListRemoved(ownerList, list);


            this.ReactToElementVariableChange += HandleElementVariableChanged;
            this.ReactToNamedObjectChangedValueList += (changeList) => _refreshManager.ReactToNamedObjectChangedValueList(changeList, AssignOrRecordOnly.Assign);
            this.ReactToNamedObjectChangedValue += HandleNamedObjectVariableOrPropertyChanged;
            this.ReactToChangedStartupScreen += ToolbarController.Self.ReactToChangedStartupScreen;
            this.ReactToItemsSelected += HandleItemsSelected;
            //this.ReactToObjectContainerChanged += _refreshManager.HandleObjectContainerChanged;
            this.ReactToObjectListContainerChanged += _refreshManager.HandleObjectListContainerChanged;
            // If a variable is added, that may be used later to control initialization.
            // The game won't reflect that until it has been restarted, so let's just take 
            // care of it now. For variable removal I don't know if any restart is needed...
            this.ReactToVariableAdded += _refreshManager.HandleVariableAdded;
            this.ReactToChangedPropertyHandler += (changedMember, oldValue, owner) =>
            {
                if(changedMember == nameof(CustomVariable.Name) && GlueState.Self.CurrentCustomVariable != null)
                {
                    _refreshManager.HandleVariableRenamed(GlueState.Self.CurrentCustomVariable);
                }
            };
            this.ReactToStateCreated += _refreshManager.HandleStateCreated;
            this.ReactToStateVariableChanged += _refreshManager.HandleStateVariableChanged;
            this.ReactToStateCategoryExcludedVariablesChanged += _refreshManager.HandleStateCategoryExcludedVariablesChanged;
            //this.ReactToMainWindowMoved += gameHostView.ReactToMainWindowMoved;
            this.ReactToMainWindowResizeEnd += gameHostView.ReactToMainWindowResizeEnd;
            this.TryHandleTreeNodeDoubleClicked += _refreshManager.HandleTreeNodeDoubleClicked;
            this.GrabbedTreeNodeChanged += HandleGrabbedTreeNodeChanged;

            this.ReactToLoadedGlux += () => pluginTab.Show();
            this.ReactToUnloadedGlux += () => pluginTab.Hide();
            this.ReactToTreeViewRightClickHandler += HandleTreeViewRightClick;

            this.ReactToSelectedSubIndexChanged += (index) => _refreshManager.ReactToSelectedSubIndexChanged(index);

            this.ReactToObjectReordered += _refreshManager.HandleObjectReordered;
        }

        private void HandleTreeViewRightClick(ITreeNode rightClickedTreeNode, List<GeneralToolStripMenuItem> listToAddTo)
        {
            //var tag = rightClickedTreeNode.Tag;

            //if(tag != null)
            //{
            //    if(tag is StateSave asStateSave)
            //    {
            //        listToAddTo.Add("Add State to Edit Toolbar", HandleAddStateToEditToolbar);
            //    }
            //}
        }

        //private async void HandleAddStateToEditToolbar(object sender, EventArgs e)
        //{
        //    var state = GlueState.Self.CurrentStateSave;
        //    var entitySave = GlueState.Self.CurrentEntitySave;
        //    var namedObject = GlueState.Self.CurrentNamedObjectSave;



        //    HandleAddToEditToolbar(state, entitySave, namedObject);

        //}

        bool AlreadyHasMatch(NamedObjectSave newNos)
        {
            var namedObjectsWithMatchingEntity = CompilerViewModel.ToolbarEntitiesAndStates.Where(item =>
                item.NamedObjectSave.SourceClassType == newNos.SourceClassType)
                .ToArray();

            var hasMatch = false;

            // If we have a match, see if the matches have the same variables, excluding X,Y,Z
            if(namedObjectsWithMatchingEntity.Length > 0)
            {
                foreach(var existing in namedObjectsWithMatchingEntity)
                {
                    if(IsMatchOnVariables(existing.NamedObjectSave, newNos))
                    {
                        hasMatch = true;
                    }
                }
            }

            return hasMatch;
        }

        private bool IsMatchOnVariables(NamedObjectSave existing, NamedObjectSave newNos)
        {
            var nonXYZExisting = ToolbarEntityViewModelManager.ExceptXYZ(existing.InstructionSaves);
            var nonXYZNew = ToolbarEntityViewModelManager.ExceptXYZ(newNos.InstructionSaves);

            if(nonXYZExisting.Length != nonXYZNew.Length)
            {
                return false;
            }

            for(int i = 0; i < nonXYZExisting.Length; i++)
            {
                if (nonXYZExisting[i].Member != nonXYZNew[i].Member || nonXYZExisting[i].Value != nonXYZNew[i].Value)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Adds a new entity to the edit toolbar. This is the side-bar which lets users create
        /// new NamedObjectSave instances quidckly during edit mode.
        /// </summary>
        /// <param name="namedObject">The NamedObject to use as a template.</param>
        /// <param name="customPreviewLocation">The on-disk location of a custom image for the icon. If null, one will be generated automatically.</param>
        private void HandleAddToEditToolbar(NamedObjectSave namedObject, string customPreviewLocation = null)
        {

            //////////////////////////Early Out////////////////////////////
            var alreadyHasMatch = AlreadyHasMatch(namedObject);
            if(alreadyHasMatch)
            {
                return;
            }
            ////////////////////////End Early Out//////////////////////////

            var newViewModel = ToolbarEntityViewModelManager.CreateNewViewModel(namedObject, customPreviewLocation);


            CompilerViewModel.ToolbarEntitiesAndStates.Add(newViewModel);
            
        }

        private void HandlePropertyChanged(string changedMember, object oldValue, GlueElement glueElement)
        {
            var currentEntity = glueElement as EntitySave;
            if(changedMember == nameof(EntitySave.CreatedByOtherEntities) && currentEntity != null)
            {
                GlueCommands.Self.GenerateCodeCommands.GenerateGame1();
            }
        }

        private void HandleElementVariableChanged(IElement element, CustomVariable variable)
        {
            _refreshManager.HandleVariableChanged(element, variable);
        }

        private void HandleNamedObjectVariableOrPropertyChanged(string changedMember, object oldValue, NamedObjectSave namedObject)
        {
            _refreshManager.HandleNamedObjectVariableOrPropertyChanged(changedMember, oldValue, namedObject, Dtos.AssignOrRecordOnly.Assign);
        }

        private void HandleItemsSelected(List<ITreeNode> selectedTreeNodes)
        {
            _refreshManager.HandleItemSelected(selectedTreeNodes);
            this.gameHostView.UpdateToItemSelected();
        }


        #endregion

        #region Public events (called externally)

        public async Task BuildAndRun()
        {
            if (CompilerViewModel.IsToolbarPlayButtonEnabled)
            {
                GlueCommands.Self.DialogCommands.FocusTab(Localization.Texts.Build);
                var succeeded = await _gameHostController.Compile();

                if (succeeded)
                {
                    var result = await ReactToPluginEventWithReturn("ErrorPlugin_GetHasErrors", "");
                    bool hasErrors = result == "true" ? true : false;
                    if (hasErrors)
                    {
                        var runAnywayMessage = "Your project has content errors. To fix them, see the Errors tab. You can still run the game but you may experience crashes. Run anyway?";
                        var innerResult = GlueCommands.Self.DialogCommands.ShowYesNoMessageBox(runAnywayMessage);
                        
                        if(innerResult == MessageBoxResult.Yes)
                        {
                            await PluginManager.CallPluginMethodAsync("Compiler Plugin", "DoRun",
                                false, // preventFocus
                                string.Empty, new GeneralResponse());
                            CompilerViewModel.IsEditChecked = false;
                        }
                    }
                    else
                    {
                        PluginManager.ReceiveOutput("Building succeeded. Running project...");

                        CompilerViewModel.IsEditChecked = false;
                        await PluginManager.CallPluginMethodAsync("Compiler Plugin", "DoRun",
                            false, // preventFocus
                            string.Empty, new GeneralResponse());
                    }
                }
                else
                {
                    GlueCommands.Self.DialogCommands.FocusTab(Localization.Texts.Build);
                }
            }
        }

        public bool GetIfIsRunningInEditMode()
        {
            return CompilerViewModel.IsEditChecked && CompilerViewModel.IsRunning;
        }

        public bool GetIfIsRunning() => CompilerViewModel.IsRunning;

        public async Task<string> MakeGameBorderless(bool isBorderless)
        {
            var dto = new Dtos.SetBorderlessDto
            {
                IsBorderless = isBorderless
            };

            var sendResponse = await CommandSender.Self.Send(dto);
            return sendResponse.Succeeded ? sendResponse.Data : String.Empty;
        }

        #endregion


        private void HandleGluxUnloaded()
        {
            CompilerViewModel.HasLoadedGlux = false;
            CompilerViewModel.ToolbarEntitiesAndStates.Clear();

            glueViewSettingsTab.Hide();

            gameHostView.HandleGluxUnloaded();


            ToolbarController.Self.HandleGluxUnloaded();
        }

        private CompilerSettingsModel LoadOrCreateCompilerSettings()
        {
            CompilerSettingsModel compilerSettings = null;
            var filePath = JsonSettingsFilePath;
            if (filePath.Exists())
            {
                try
                {
                    var text = System.IO.File.ReadAllText(filePath.FullPath);
                    compilerSettings = JsonConvert.DeserializeObject<CompilerSettingsModel>(text);
                }
                catch
                {
                    // do nothing, it'll just get wiped out and re-saved later
                }
            }

            if(compilerSettings == null)
            {
                var random = new Random();
                compilerSettings = new CompilerSettingsModel();
                compilerSettings.SetDefaults();
                // randomize it a little to reduce the likelihood of it being the same as a different game.
                // Before, it was always 8021
                compilerSettings.PortNumber = 8000 + random.Next(1000);
            }

            return compilerSettings;
        }

        

        private bool IsFrbNewEnough()
        {
            var mainProject = GlueState.Self.CurrentMainProject;
            if(mainProject.IsFrbSourceLinked())
            {
                return true;
            }
            else
            {
                return GlueState.Self.CurrentGlueProject.FileVersion >= (int)GlueProjectSave.GluxVersions.SupportsEditMode;
            }
        }

        private void HandleGluxLoaded()
        {
            var model = LoadOrCreateCompilerSettings();
            ignoreViewModelChanges = true;
            GlueViewSettingsViewModel.SetFrom(model);
            glueViewSettingsView.DataUiGrid.Refresh();

            CompilerViewModel.IsGenerateGlueControlManagerInGame1Checked = GlueViewSettingsViewModel.EnableLiveEdit;
            ignoreViewModelChanges = false;

            CompilerViewModel.IsGluxVersionNewEnoughForGlueControlGeneration =
                GlueState.Self.CurrentGlueProject.FileVersion >= (int)GlueProjectSave.GluxVersions.AddedGeneratedGame1;
            CompilerViewModel.ToolbarEntitiesAndStates.Clear();

            CompilerViewModel.HasLoadedGlux = true;
            //CompilerViewModel.SetFrom(model);
            foreach (var toolbarModel in model.ToolbarObjects)
            {
                HandleAddToEditToolbar(toolbarModel.NamedObject, toolbarModel.CustomPreviewLocation);

            }

            game1GlueControlGenerator.PortNumber = model.PortNumber;
            game1GlueControlGenerator.IsGlueControlManagerGenerationEnabled = model.GenerateGlueControlManagerCode && IsFrbNewEnough();

            MainGameCommunicationPlugin.Self.SetPrimarySettings(
                game1GlueControlGenerator.PortNumber,
                game1GlueControlGenerator.IsGlueControlManagerGenerationEnabled
                );

            _refreshManager.PortNumber = model.PortNumber;

            ToolbarController.Self.HandleGluxLoaded();

            if(IsFrbNewEnough() && GlueViewSettingsViewModel.EnableLiveEdit)
            {
                TaskManager.Self.Add(() => EmbeddedCodeManager.EmbedAll(model.GenerateGlueControlManagerCode), "Generate Glue Control Code");
                TaskManager.Self.Add(() => GlueCallsCodeGenerator.GenerateAll(), "Generate Glue Control Code New");
            }
            else
            {
                TaskManager.Self.Add(() => EmbeddedCodeManager.RemoveAll(), "Removing Glue Control Code");
                TaskManager.Self.Add(() => GlueCallsCodeGenerator.RemoveAll(), "Removing Glue Control Code New");

            }

            AddNewtonsoft();

            gameHostView.HandleGluxLoaded();
        }

        #region Game Tab

        private void CreateGameTab()
        {

            // winforms stuff is here:
            // https://social.msdn.microsoft.com/Forums/en-US/f6e28fe1-03b2-4df5-8cfd-7107c2b6d780/hosting-external-application-in-windowsformhost?forum=wpf
            gameHostView = new GameHostView(ReactToPluginEventWithReturn, ReactToPluginEvent);


            gameHostView.DataContext = CompilerViewModel;
            gameHostView.TreeNodedDroppedInEditBar += (treeNode) =>
            {
                // todo - handle this:
                //if(treeNode.Tag is StateSave stateSave)
                //{
                //    var container = ObjectFinder.Self.GetElementContaining(stateSave);
                //    if(container is EntitySave entitySave)
                //    {
                //        HandleAddToEditToolbar(stateSave, entitySave, null);
                //    }
                //}
                //else 
                if (treeNode.Tag is EntitySave entitySave)
                {
                    var namedObjectSave = new NamedObjectSave();
                    namedObjectSave.SourceType = SourceType.Entity;
                    namedObjectSave.SourceClassType = entitySave.Name;
                    // todo finish here
                    HandleAddToEditToolbar(namedObjectSave);
                }
                else if (treeNode.Tag is NamedObjectSave nos)
                {
                    HandleAddToEditToolbar(nos);
                }
            };

            pluginTab = base.CreateTab(gameHostView, Localization.Texts.Game, TabLocation.Center);
            pluginTab.CanClose = false;
            pluginTab.AfterHide += (_, __) => TryKillGame();
            //pluginTab = base.CreateAndAddTab(GameHostView, "Game Contrll", TabLocation.Bottom);

            _gameHostController = new GameHostController();
            _gameHostController.Initialize(gameHostView, (value) => PluginManager.CallPluginMethod("Compiler Plugin", "HandleOutput", value),
                CompilerViewModel,
                GlueViewSettingsViewModel,
                glueViewSettingsTab,
                ReactToPluginEventWithReturn,
                _refreshManager);

            #region Start the timer, do it after the gameHostView is created

            // 250 works but you can feel the delay a bit when working in game. Let's try 150
            //var busyTimerFrequency = 250; // ms
            //var busyTimerFrequency = 150; // ms
            // even faster?
            //var busyTimerFrequency = 100; // ms
            //busyUpdateTimer = new Timer(busyTimerFrequency);
            //busyUpdateTimer.Elapsed += async (not, used) => await DoGetCommandsTimedLogic();
            //busyUpdateTimer.SynchronizingObject = MainGlueWindow.Self;
            //busyUpdateTimer.Start();

            // This was 250 but it wasn't fast enough to feel responsive
            var dragDropTimerFrequency = 100; // ms
            dragDropTimer = new Timer(dragDropTimerFrequency);
            dragDropTimer.Elapsed += (not, used) => _dragDropManagerGameWindow.HandleDragDropTimerElapsed(gameHostView);
            dragDropTimer.SynchronizingObject = MainGlueWindow.Self;
            dragDropTimer.Start();

            #endregion
        }

        #endregion

        #region Toolbar

        private void CreateToolbar()
        {
            var toolbar = new RunnerToolbar();
            toolbar.RunClicked += HandleToolbarRunClicked;

            ToolbarController.Self.Initialize(toolbar);

            toolbar.DataContext = ToolbarController.Self.GetViewModel();

            base.AddToToolBar(toolbar, "Standard");
        }

        private async void HandleToolbarRunClicked(object sender, EventArgs e)
        {
            // force the view model to not be in edit mode if this was clicked
            CompilerViewModel.PlayOrEdit = PlayOrEdit.Play;
            await BuildAndRun();
        }

        #endregion

        #region Profile Tab

        void CreateProfileTab()
        {
            var control = new ProfilingControl();
            var vm = new ProfilingControlViewModel();
            control.DataContext = vm;

            this.CreateAndAddTab(control, Localization.Texts.Profiling, TabLocation.Bottom);

            ProfilingManager.Self.Initialize(vm, CompilerViewModel);
        }

        #endregion

        private void CreateBuildControl()
        {
            CompilerViewModel = CompilerViewModel.Self;
            CompilerViewModel.Configuration = "Debug";
            GlueViewSettingsViewModel = new GlueViewSettingsViewModel();
            GlueViewSettingsViewModel.PropertyChanged += HandleGlueViewSettingsViewModelPropertyChanged;
            CompilerViewModel.PropertyChanged += HandleCompilerViewModelPropertyChanged;

            MainViewModel = CompilerViewModel;

            //MainControl = new BuildTabView();
            //MainControl.DataContext = CompilerViewModel;

            _refreshManager.ViewModel = CompilerViewModel;
            _dragDropManagerGameWindow.CompilerViewModel = CompilerViewModel;
            _commandReceiver.CompilerViewModel = CompilerViewModel;
            _commandReceiver.PrintOutput = (value) => PluginManager.CallPluginMethod("Compiler Plugin", "HandleOutput", value);
            _refreshManager.GlueViewSettingsViewModel = GlueViewSettingsViewModel;

            _variableSendingManager.ViewModel = CompilerViewModel;
            _variableSendingManager.GlueViewSettingsViewModel = GlueViewSettingsViewModel;

            CommandSender.Self.GlueViewSettingsViewModel = GlueViewSettingsViewModel;
            CommandSender.Self.CompilerViewModel = CompilerViewModel;
            CommandSender.Self.PrintOutput = (value) => PluginManager.CallPluginMethod("Compiler Plugin", "HandleOutput", value);
            CommandSender.Self.SendPacket = async (value, waitForResponse) =>
            {
                var whatToSend = new GameJsonCommunicationPlugin.Common.GameConnectionManager.Packet
                {
                    PacketType = "OldDTO",
                    Payload = value
                };

                if(waitForResponse)
                {
                    var toReturn = new global::ToolsUtilities.GeneralResponse<string>();
                    try
                    {
                        var response = await GameJsonCommunicationPlugin.Common.GameConnectionManager.Self.SendItemWithResponse(whatToSend);

                        toReturn.SetFrom(response);
                        toReturn.Data = response?.Data;    
                    }
                    catch(Exception e)
                    {
                        toReturn.Succeeded = false;
                        toReturn.Message = $"Failed to send packet: {e}";
                    }

                    return toReturn;
                }
                else
                {
                    await GameJsonCommunicationPlugin.Common.GameConnectionManager.Self.SendItem(whatToSend);
                    // I guess we return success?
                    return new global::ToolsUtilities.GeneralResponse<string>() { Succeeded=true };
                }

            };
                //ReactToPluginEventWithReturn("GameCommunication_Send_OldDTO", value);
            
            glueViewSettingsView = new Views.GlueViewSettings();
            glueViewSettingsView.ViewModel = GlueViewSettingsViewModel;

            glueViewSettingsTab = base.CreateTab(glueViewSettingsView, Localization.Texts.EditorSettings);

            AssignControlEvents();
        }

        private async void HandleGlueViewSettingsViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            //////////Early Out////////////////////
            if (ignoreViewModelChanges)
            {
                return;
            }

            /////////End Early Out//////////////// 
            var propertyName = e.PropertyName;
            switch(propertyName)
            {
                case nameof(ViewModels.GlueViewSettingsViewModel.PortNumber):
                case nameof(ViewModels.GlueViewSettingsViewModel.EnableLiveEdit):
                    CompilerViewModel.IsGenerateGlueControlManagerInGame1Checked = GlueViewSettingsViewModel.EnableLiveEdit;
                    await HandlePortOrGenerateCheckedChanged(propertyName);
                    break;
                case nameof(ViewModels.GlueViewSettingsViewModel.ShowGrid):
                case nameof(ViewModels.GlueViewSettingsViewModel.GridAlpha):
                case nameof(ViewModels.GlueViewSettingsViewModel.GridSize):
                case nameof(ViewModels.GlueViewSettingsViewModel.SetBackgroundColor):
                case nameof(ViewModels.GlueViewSettingsViewModel.BackgroundRed):
                case nameof(ViewModels.GlueViewSettingsViewModel.BackgroundGreen):
                case nameof(ViewModels.GlueViewSettingsViewModel.BackgroundBlue):
                case nameof(ViewModels.GlueViewSettingsViewModel.SnapSize):
                case nameof(ViewModels.GlueViewSettingsViewModel.EnableSnapping):
                case nameof(ViewModels.GlueViewSettingsViewModel.PolygonPointSnapSize):
                case nameof(ViewModels.GlueViewSettingsViewModel.ShowScreenBoundsWhenViewingEntities):
                    await SendGlueViewSettingsToGame();
                    break;
            }


            SaveCompilerSettingsModel();

        }

        private async Task SendGlueViewSettingsToGame()
        {
            var dto = new Dtos.GlueViewSettingsDto
            {
                ShowGrid = GlueViewSettingsViewModel.ShowGrid,
                GridAlpha = GlueViewSettingsViewModel.GridAlpha,
                GridSize = GlueViewSettingsViewModel.GridSize,
                ShowScreenBoundsWhenViewingEntities = GlueViewSettingsViewModel.ShowScreenBoundsWhenViewingEntities,
                SetBackgroundColor = GlueViewSettingsViewModel.SetBackgroundColor,
                BackgroundRed = GlueViewSettingsViewModel.BackgroundRed,
                BackgroundGreen = GlueViewSettingsViewModel.BackgroundGreen,
                BackgroundBlue = GlueViewSettingsViewModel.BackgroundBlue,
                EnableSnapping = GlueViewSettingsViewModel.EnableSnapping,
                SnapSize = GlueViewSettingsViewModel.SnapSize,
                PolygonPointSnapSize = GlueViewSettingsViewModel.PolygonPointSnapSize,
            };

            await CommandSender.Self.Send(dto);
        }

        private async void HandleCompilerViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            //////////Early Out////////////////////
            if (ignoreViewModelChanges)
            {
                return;
            }

            /////////End Early Out////////////////
            var propertyName = e.PropertyName;

            switch (propertyName)
            {

                case nameof(CompilerViewModel.CurrentGameSpeed):
                    var speedPercentage = int.Parse(CompilerViewModel.CurrentGameSpeed.Substring(0, CompilerViewModel.CurrentGameSpeed.Length - 1));
                    await CommandSender.Self.Send(new SetSpeedDto
                    {
                        SpeedPercentage = speedPercentage
                    }, waitForResponse:false);
                    
                    break;
                case nameof(CompilerViewModel.EffectiveIsRebuildAndRestartEnabled):
                    _refreshManager.IsExplicitlySetRebuildAndRestartEnabled = CompilerViewModel.EffectiveIsRebuildAndRestartEnabled;
                    break;
                case nameof(CompilerViewModel.IsToolbarPlayButtonEnabled):
                    ToolbarController.Self.SetEnabled(CompilerViewModel.IsToolbarPlayButtonEnabled);
                    break;
                case nameof(CompilerViewModel.IsRunning):
                    //CommandSender.CancelConnect();
                    break;
                case nameof(CompilerViewModel.PlayOrEdit):

                    await ReactToPlayOrEditSet();

                    break;
                case nameof(CompilerViewModel.ToolbarEntitiesAndStates):
                    if(CompilerViewModel.HasLoadedGlux)
                    {
                        SaveCompilerSettingsModel();
                    }
                    break;
            }


        }

        private async Task ReactToPlayOrEditSet()
        {
            var inEditMode = CompilerViewModel.PlayOrEdit == PlayOrEdit.Edit;
            var dto = new Dtos.SetEditMode 
            { 
                IsInEditMode = inEditMode ,
                AbsoluteGlueProjectFilePath = GlueState.Self.GlueProjectFileName?.FullPath
            };
            var response = await CommandSender.Self.Send<Dtos.GeneralCommandResponse>(dto, SendImportance.RetryOnFailure);

            if (response?.Succeeded != true)
            {
                var message = string.Format($"Failed to set game/edit mode to {CompilerViewModel.PlayOrEdit}\n");
                if (response == null)
                {
                    message += "Game sent back no response";
                }
                else
                {
                    message += $"Game sent back the following message: {response.Message}";
                }
                PluginManager.CallPluginMethod("Compiler Plugin", "HandleOutput", message);
                
                GlueCommands.Self.PrintOutput(message);
            }
            else if (CommandSender.Self.IsConnected == false)
            {
                var message = $"Failed to set game/edit mode to {CompilerViewModel.PlayOrEdit} because the game is not connected.";
            }
            else if (inEditMode)
            {
                var currentEntity = GlueState.Self.CurrentEntitySave;
                if (currentEntity != null)
                {
                    await _refreshManager.PushGlueSelectionToGame();
                }
                else
                {
                    var screenName = await CommandSender.Self.GetScreenName();

                    if (!string.IsNullOrEmpty(screenName))
                    {
                        var glueScreenName =
                            string.Join('\\', screenName.Split('.').Skip(1).ToArray());

                        var screen = ObjectFinder.Self.GetScreenSave(glueScreenName);

                        if (screen != null)
                        {

                            if (GlueState.Self.CurrentElement != screen)
                            {
                                GlueState.Self.CurrentElement = screen;

                            }
                            else
                            {
                                // the screens are the same, so push the object selection from Glue to the game:
                                await _refreshManager.PushGlueSelectionToGame();
                            }
                        }
                    }
                }

                await SendGlueViewSettingsToGame();
            }
            else
            {
                // the user is viewing an entity, so force the screen
                if (GlueState.Self.CurrentEntitySave != null)
                {
                    // push the selection to game
                    var startupScreen = ObjectFinder.Self.GetScreenSave(GlueState.Self.CurrentGlueProject.StartUpScreen);
                    await _refreshManager.PushGlueSelectionToGame(forcedElement: startupScreen);
                }
            }
            var setCameraAspectRatioDto = new SetCameraAspectRatioDto();

            var displaySettings = GlueState.Self.CurrentGlueProject?.DisplaySettings;

            if (inEditMode)
            {
                setCameraAspectRatioDto.AspectRatio = null;
            }
            else
            {
                if(displaySettings != null &&
                    displaySettings.AspectRatioHeight > 0 &&
                    // need to reearch at some time - do we want to worry about variable aspect ratio?
                    displaySettings.AspectRatioBehavior == AspectRatioBehavior.FixedAspectRatio)
                {
                    setCameraAspectRatioDto.AspectRatio = GlueState.Self.CurrentGlueProject.DisplaySettings.AspectRatioWidth /
                        GlueState.Self.CurrentGlueProject.DisplaySettings.AspectRatioHeight;
                }
            }
                    
            await CommandSender.Self.Send(setCameraAspectRatioDto);
        }

        private async Task HandlePortOrGenerateCheckedChanged(string propertyName)
        {
            PluginManager.CallPluginMethod("Compiler Plugin", "HandleOutput", "Applying Changes");
            game1GlueControlGenerator.IsGlueControlManagerGenerationEnabled = GlueViewSettingsViewModel.EnableLiveEdit && IsFrbNewEnough();
            game1GlueControlGenerator.PortNumber = GlueViewSettingsViewModel.PortNumber;
            _refreshManager.PortNumber = GlueViewSettingsViewModel.PortNumber;

            MainGameCommunicationPlugin.Self.SetPrimarySettings(
                    game1GlueControlGenerator.PortNumber,
                    game1GlueControlGenerator.IsGlueControlManagerGenerationEnabled
            );

            GlueCommands.Self.GenerateCodeCommands.GenerateGame1();
            if (IsFrbNewEnough() && GlueViewSettingsViewModel.EnableLiveEdit)
            {
                TaskManager.Self.Add(() => EmbeddedCodeManager.EmbedAll(GlueViewSettingsViewModel.EnableLiveEdit), Localization.Texts.GenerateGlueControlCode);
                TaskManager.Self.Add(() => GlueCallsCodeGenerator.GenerateAll(), Localization.Texts.GenerateNewGlueControlCode);
            }
            else
            {
                TaskManager.Self.Add(() => EmbeddedCodeManager.RemoveAll(), "Removing Glue Control Code");
                TaskManager.Self.Add(() => GlueCallsCodeGenerator.RemoveAll(), "Removing Glue Control Code New");

            }

            if (GlueState.Self.CurrentGlueProject.FileVersion >= (int)GlueProjectSave.GluxVersions.NugetPackageInCsproj)
            {
                AddNewtonsoft();
            }

            _refreshManager.CreateStopAndRestartTask($"{propertyName} changed");

            PluginManager.CallPluginMethod("Compiler Plugin", "HandleOutput", "Waiting for tasks to finish...");
            await TaskManager.Self.WaitForAllTasksFinished();

            PluginManager.CallPluginMethod("Compiler Plugin", "HandleOutput", "Finishined adding/generating code for GlueControlManager");
        }

        private static void AddNewtonsoft()
        {
            GlueCommands.Self.ProjectCommands.AddNugetIfNotAdded("Newtonsoft.Json", "13.0.3");
        }

        private void SaveCompilerSettingsModel()
        {
            var model = new CompilerSettingsModel();
            GlueViewSettingsViewModel.SetModel(model);

            foreach(var vm in CompilerViewModel.ToolbarEntitiesAndStates)
            {
                var toolbarModel = new ToolbarModel();
                vm.ApplyTo(toolbarModel);
                model.ToolbarObjects.Add(toolbarModel);
            }

            try
            {
                var text = JsonConvert.SerializeObject(model);
                GlueCommands.Self.TryMultipleTimes(() =>
                {
                    System.IO.Directory.CreateDirectory(JsonSettingsFilePath.GetDirectoryContainingThis().FullPath);

                    GlueCommands.Self.FileCommands.SaveIfDiffers(JsonSettingsFilePath, text);
                });
            }
            catch
            {
                // no big deal if it fails
            }
        }

        private void AssignControlEvents()
        {
            //MainControl.BuildClicked += async (not, used) =>
            //{
            //    var succeeded = await GameHostController.Self.Compile();
            //    if(!succeeded)
            //    {
            //        GlueCommands.Self.DialogCommands.FocusTab("Build");
            //    }
            //};


            //MainControl.RunClicked += async (not, used) =>
            //{
            //    var succeeded = await GameHostController.Self.Compile();
            //    if (succeeded)
            //    {
            //        if (succeeded)
            //        {
            //            CompilerViewModel.IsRunning = false;
            //            await runner.Run(preventFocus: false);
            //        }
            //        else
            //        {
            //            var runAnywayMessage = "Your project has content errors. To fix them, see the Errors tab. You can still run the game but you may experience crashes. Run anyway?";

            //            GlueCommands.Self.DialogCommands.ShowYesNoMessageBox(runAnywayMessage, async () => await runner.Run(preventFocus: false));
            //        }
            //    }
            //};

            //MainControl.MSBuildSettingsClicked += () =>
            //{
            //    var viewModel = new BuildSettingsWindowViewModel();
            //    var view = new BuildSettingsWindow();
            //    view.DataContext = viewModel;
            //    viewModel.SetFrom(BuildSettingsUser);

            //    var results = view.ShowDialog();

            //    if(results == true)
            //    {
            //        // apply VM:
            //        viewModel.ApplyTo(BuildSettingsUser);

            //        GlueCommands.Self.TryMultipleTimes(() =>
            //        {
            //            var textToSave = JsonConvert.SerializeObject(BuildSettingsUser);
            //            System.IO.File.WriteAllText(BuildSettingsUserFilePath.FullPath, textToSave);
            //        });
            //    }
            //};
        }

        private void OutputSuccessOrFailure(bool succeeded)
        {
            if (succeeded)
            {
                PluginManager.CallPluginMethod("Compiler Plugin", "HandleOutput", $"{DateTime.Now.ToLongTimeString()} Build succeeded");

            }
            else
            {
                PluginManager.CallPluginMethod("Compiler Plugin", "HandleOutput", $"{DateTime.Now.ToLongTimeString()} Build failed");
            }
        }

        public override bool ShutDown(PluginShutDownReason shutDownReason)
        {
            TryKillGame();
            return true;
        }


        public async void ShowState(string stateName, string categoryName)
        {
            await _refreshManager.PushGlueSelectionToGame(categoryName, stateName);
        }

        public void SetIsLoggingSentCommands(bool isLoggingSentCommands)
        {
            CompilerViewModel.IsPrintEditorToGameCheckboxChecked = isLoggingSentCommands;
        }


        private void HandleGrabbedTreeNodeChanged(ITreeNode treeNode, TreeNodeAction action)
        {


        }


        #region DLLImports
        [DllImport("user32.dll")]
        static extern int SetWindowLong(IntPtr hWnd, int nIndex, UInt32 dwNewLong);

        // from https://docs.microsoft.com/en-us/dotnet/desktop/wpf/advanced/walkthrough-hosting-a-win32-control-in-wpf?view=netframeworkdesktop-4.8
        internal const int
          WS_CHILD = 0x40000000,
          WS_VISIBLE = 0x10000000,
          LBS_NOTIFY = 0x00000001,
          HOST_ID = 0x00000002,
          LISTBOX_ID = 0x00000001,
          WS_VSCROLL = 0x00200000,
          WS_BORDER = 0x00800000;

        [DllImport("user32.dll")]
        static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        #endregion

        #region Fields/Properties


        PluginTab pluginTab;
        GameHostView gameHostView;

        Process gameProcess;
        private GameHostController _gameHostController;
        
        private RefreshManager _refreshManager;
        private DragDropManagerGameWindow _dragDropManagerGameWindow;
        private VariableSendingManager _variableSendingManager;
        private CommandReceiver _commandReceiver;
        public CommandReceiver CommandReceiver => _commandReceiver;

        #endregion

        public async void MoveGameToHost()
        {
            gameProcess = TryFindGameProcess();
            var handle = gameProcess?.MainWindowHandle;

            if (handle != null)
            {
                await gameHostView.EmbedHwnd(handle.Value);

                GlueCommands.Self.DoOnUiThread(() =>
                {
                    CompilerViewModel.IsWindowEmbedded = true;
                });

                // sometimes the game doesn't embed itself properly. To fix this, we can resize the window:
                await Task.Delay(50);

                await GlueCommands.Self.DoOnUiThread(async () =>
                {
                    await gameHostView.ForceRefreshGameArea(force: true);
                });
            }
            else
            {
                GlueCommands.Self.DoOnUiThread(() => CompilerViewModel.IsWindowEmbedded = false);
                if(gameProcess == null)
                {
                    GlueCommands.Self.PrintOutput(Localization.Texts.FailedFindGameHandle);
                }
                else
                {
                    GlueCommands.Self.PrintOutput(Localization.Texts.FailedFindWindowHandle);
                }
                
            }
        }


        private void TryKillGame()
        {
            if (gameProcess != null)
            {
                try
                {
                    gameProcess?.Kill();
                }
                catch
                {
                    // no biggie, It hink
                }
            }
        }

        private Process TryFindGameProcess(bool mustHaveWindow = true)
        {
            // find a process for game
            var processes = Process.GetProcesses()
                .OrderBy(item => item.ProcessName)
                .ToArray();

            var projectName = 
                GlueState.Self.CurrentMainProject?.ExecutableName?.ToLowerInvariant();

            var found = processes
                .FirstOrDefault(item => item.ProcessName.ToLowerInvariant() == projectName &&
                (mustHaveWindow == false || item.MainWindowHandle != IntPtr.Zero));
            return found;
        }

        #region Override Methods
        public override void HandleEvent(string eventName, string payload)
        {
            base.HandleEvent(eventName, payload);

            switch(eventName)
            {
                case "Runner_GameStarted":
                    Task.Run(async () =>
                    {
                        // If we aren't generating the code, we shouldn't try to move the game to Glue since the borders can't be adjusted
                        if (CompilerViewModel.IsGenerateGlueControlManagerInGame1Checked && GlueViewSettingsViewModel.EmbedGameInGameTab)
                        {
                            MoveGameToHost();
                        }


                        if (CompilerViewModel.PlayOrEdit == PlayOrEdit.Edit)
                        {
                            await ReactToPlayOrEditSet();
                        }
                    });

                    break;
                case "GameCommunication_Connected":
                    CommandSender.Self.IsConnected = true;

                    break;

                case "GameCommunication_Disconnected":
                    CommandSender.Self.IsConnected = false;

                    break;

                //case "GlueControl_SelectObject":

                //    _commandReceiver.HandleSelectObject(JsonConvert.DeserializeObject<SelectObjectDto>(payload));

                //    break;

            }
        }
        #endregion



    }

}
