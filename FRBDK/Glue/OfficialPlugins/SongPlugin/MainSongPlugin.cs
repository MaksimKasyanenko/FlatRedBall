﻿using FlatRedBall.Glue.CodeGeneration;
using FlatRedBall.Glue.Controls;
using FlatRedBall.Glue.Elements;
using FlatRedBall.Glue.FormHelpers;
using FlatRedBall.Glue.IO;
using FlatRedBall.Glue.Plugins;
using FlatRedBall.Glue.Plugins.ExportedImplementations;
using FlatRedBall.Glue.Plugins.Interfaces;
using FlatRedBall.Glue.SaveClasses;
using FlatRedBall.IO;
using OfficialPlugins.SongPlugin.CodeGenerators;
using OfficialPlugins.SongPlugin.ViewModels;
using OfficialPlugins.SongPlugin.Views;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace OfficialPlugins.SongPlugin
{
    [Export(typeof(PluginBase))]
    public class MainSongPlugin : PluginBase
    {
        #region Fields/Properties

        MainSongControlViewModel viewModel;
        MainSongControl control;
        PluginTab pluginTab;


        public override string FriendlyName
        {
            get { return "Song Plugin"; }
        }

        public override Version Version
        {
            get { return new Version(1, 0); }
        }

        #endregion

        public override bool ShutDown(PluginShutDownReason shutDownReason)
        {
            return true;
        }

        public override void StartUp()
        {
            viewModel = new MainSongControlViewModel();

            AdjustAssetTypeInfo();

            CreateCodeGenerator();

            AssignEvents();
        }

        private void AssignEvents()
        {
            this.ReactToItemSelectHandler += HandleItemSelected;
            this.ReactToLoadedGluxEarly += HandleGluxLoadEarly;
            this.ReactToFileChange += HandleFileChanged;
            this.TryHandleTreeNodeDoubleClicked += TryHandleDoubleClick;
        }

        private bool TryHandleDoubleClick(ITreeNode arg)
        {
            var rfs = arg.Tag as ReferencedFileSave;

            if(IsSong(rfs))
            {
                pluginTab?.Focus();
                control.PlaySong();

                return true;
            }

            return false;
        }

        #region AssetTypeInfo

        AssetTypeInfo[] SongAtis => AvailableAssetTypes.Self.AllAssetTypes
                .Where(item => item.QualifiedRuntimeTypeName.QualifiedType == "Microsoft.Xna.Framework.Media.Song")
                .ToArray();

        private void AdjustAssetTypeInfo()
        {
            var atis = SongAtis;

            foreach (var ati in atis)
            {
                ati.CustomLoadFunc = (element, nos, rfs, contentManager) =>
                {
                    bool shouldAssignField = ReferencedFileSaveCodeGenerator.NeedsFullProperty(rfs, element);

                    string variableName;

                    if (shouldAssignField)
                    {
                        variableName = "m" + rfs.GetInstanceName();
                    }
                    else
                    {
                        variableName = rfs.GetInstanceName();
                    }

                    var fileName = ReferencedFileSaveCodeGenerator.GetFileToLoadForRfs(rfs, ati); // FlatRedBall.IO.FileManager.RemoveExtension(rfs.Name).ToLowerInvariant().Replace("\\", "/");
                    string suffix = null;
                    if (rfs.DestroyOnUnload == false)
                    {
                        contentManager = "FlatRedBall.FlatRedBallServices.GlobalContentManager";
                        suffix = "/*Since this song is used on multiple screens, forcing it to use global content*/";

                    }


                    //return $"{propertyName} = FlatRedBall.FlatRedBallServices.Load<Microsoft.Xna.Framework.Media.Song>(@"content/screens/gamescreen/baronsong", contentManagerName);";
                    return $"{variableName} = FlatRedBall.FlatRedBallServices.Load<Microsoft.Xna.Framework.Media.Song>(@\"{fileName}\", {contentManager}); {suffix}";
                };

            }

        }

        #endregion

        private void CreateCodeGenerator()
        {
            var codeGenerator = new SongPluginCodeGenerator();
            this.RegisterCodeGenerator(codeGenerator);
        }


        private void HandleGluxLoadEarly()
        {
            var atis = SongAtis;

            foreach (var ati in atis)
            {
                ati.AddToManagersFunc = HandleSongAddToManagers;

            }
        }

        static bool HasISong => GlueState.Self.CurrentGlueProject.FileVersion >= (int)GlueProjectSave.GluxVersions.ISongInFrb ||
            GlueState.Self.IsReferencingFrbSource;

        private string HandleSongAddToManagers(IElement element, NamedObjectSave namedObjectSave, ReferencedFileSave rfs, string layer)
        {
            var supportsEditMode = GlueState.Self.CurrentGlueProject.FileVersion >= (int)GlueProjectSave.GluxVersions.SupportsEditMode;
            string addToManagersMethod = String.Empty;
            addToManagersMethod += "{";
            if (HasISong)
            {
                addToManagersMethod += $"var restartSong = " +
                    $"(FlatRedBall.Audio.AudioManager.CurrentlyPlayingSong == null && FlatRedBall.Audio.AudioManager.CurrentlyPlayingISong == null) || " +
                    $"FlatRedBall.Audio.AudioManager.StopAndDisposeCurrentSongIfNameDiffers(\"{rfs.GetInstanceName()}\"); ";
            }
            else
            {
                addToManagersMethod += $"var restartSong = FlatRedBall.Audio.AudioManager.CurrentlyPlayingSong == null || FlatRedBall.Audio.AudioManager.StopAndDisposeCurrentSongIfNameDiffers(\"{rfs.GetInstanceName()}\"); ";
            }
            if (supportsEditMode)
            {
                addToManagersMethod +=

                    "if(FlatRedBall.Screens.ScreenManager.IsInEditMode == false) ";
            }

            var forceGlobal = !rfs.DestroyOnUnload;

            string isGlobal = 
                forceGlobal ? "true" : "ContentManagerName == \"Global\"";

            addToManagersMethod += 
                    $"FlatRedBall.Audio.AudioManager.PlaySong({rfs.GetInstanceName()}, restartSong, {isGlobal});";
            addToManagersMethod += "}";

            return addToManagersMethod;
        }

        private void HandleItemSelected(ITreeNode selectedTreeNode)
        {
            var rfs = selectedTreeNode?.Tag as ReferencedFileSave;

            viewModel.GlueObject = rfs;

            bool shouldShowControl = false;

            if (IsSong(rfs))
            {
                viewModel.UpdateFromGlueObject();
                shouldShowControl = true;
            }

            if (shouldShowControl)
            {
                if (control == null)
                {
                    control = new MainSongControl();
                    pluginTab = this.CreateTab(control, "Song");
                    control.DataContext = viewModel;
                }

                control.FilePath = GlueCommands.Self.GetAbsoluteFilePath(rfs);

                pluginTab.Show();
            }
            else
            {
                control?.StopPlaying();
                pluginTab?.Hide();
            }
        }

        private void HandleFileChanged(FilePath filePath, FileChangeType arg2)
        {
            if(filePath == control?.FilePath)
            {
                control.ForceRefreshSongFilePath(filePath);
            }
        }

        bool IsSong(ReferencedFileSave rfs)
        {
            var ati = rfs?.GetAssetTypeInfo();

            // Use the qualified type because there are multiple ATIs this could be, so don't do an == comparison
            return ati?.QualifiedRuntimeTypeName.QualifiedType == "Microsoft.Xna.Framework.Media.Song" 
            // Update June 29, 2023
            // NAudio songs are now handled by this plugin
                || ati?.QualifiedRuntimeTypeName.QualifiedType == "FlatRedBall.NAudio.NAudio_Song"
            ;
        }

    }
}
