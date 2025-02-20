﻿using FlatRedBall.Glue.MVVM;
using FlatRedBall.Glue.Plugins.ExportedImplementations;
using FlatRedBall.Glue.ViewModels;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using static SkiaSharp.HarfBuzz.SKShaper;

namespace FlatRedBall.Glue.Controls
{
    /// <summary>
    /// Interaction logic for ListBoxWindowWpf.xaml
    /// </summary>
    public partial class ListBoxWindowWpf : Window
    {
        ListBoxWindowViewModel ViewModel => DataContext as ListBoxWindowViewModel;

        List<Button> mButtons = new List<Button>();

        public object ClickedOption { get; private set; }

        public object SelectedListBoxItem => ListBoxInstance.SelectedItem;

        public string Message
        {
            get => DisplayTextLabel.Text;
            set => DisplayTextLabel.Text = value;
        }

        public ListBoxWindowWpf()
        {
            InitializeComponent();

            DataContext = new ListBoxWindowViewModel();

            AddButton("OK", System.Windows.Forms.DialogResult.OK);

            GlueCommands.Self.DialogCommands.MoveToCursor(this);
        }

        public void AddItem(object objectToAdd)
        {
            ViewModel.AllItems.Add(objectToAdd);
            // can we make this faster?
            ViewModel.RefreshFilteredItems();
        }

        public void ShowSearchBar()
        {
            SearchBarInstance.Visibility = Visibility.Visible;
        }

        public void ClearButtons()
        {
            foreach (Button button in mButtons)
            {
                this.ButtonStackPanel.Children.Remove(button);
            }

            mButtons.Clear();

        }

        public void AddControl(UIElement element)
        {
            this.AdditionalControlStackPanel.Children.Add(element);
        }

        public void AddButton(string message, object result)
        {
            Button button = new Button();

            button.Content = message;
            button.Tag = result;
            button.Click += (not, used) =>
            {
                ClickedOption = result;
                this.DialogResult = true;
            };
            this.ButtonStackPanel.Children.Add(button);
            mButtons.Add(button);
        }

        public void ShowYesNoButtons()
        {
            this.ButtonStackPanel.Children.Clear();

            Button yesButton = new Button();

            yesButton.Content = "Yes";
            yesButton.Tag = "Yes";
            yesButton.Click += (not, used) =>
            {
                ClickedOption = "yes";
                this.DialogResult = true;
            };
            this.ButtonStackPanel.Children.Add(yesButton);
            mButtons.Add(yesButton);

            Button noButton = new Button();

            noButton.Content = "No";
            noButton.Tag = "No";
            noButton.Click += (not, used) =>
            {
                ClickedOption = "no";
                this.DialogResult = false;
            };
            this.ButtonStackPanel.Children.Add(noButton);
            mButtons.Add(noButton);



        }
    }

    public class ListBoxWindowViewModel : ViewModel, ISearchBarViewModel
    {
        public ObservableCollection<object> AllItems
        {
            get => Get<ObservableCollection<object>>();
            set => Set(value);
        }

        public ObservableCollection<object> FilteredItems
        {
            get => Get<ObservableCollection<object>>();
            set => Set(value);
        }
        
        public string SearchBoxText
        {
            get => Get<string>();
            set
            {
                if (Set(value))
                {
                    RefreshFilteredItems();
                }
            }
        }

        [DependsOn(nameof(SearchBoxText))]
        public Visibility SearchButtonVisibility => (!string.IsNullOrEmpty(SearchBoxText)).ToVisibility();

        public bool IsSearchBoxFocused
        {
            get => Get<bool>();
            set => Set(value);
        }

        public Visibility TipsVisibility => Visibility.Collapsed;

        [DependsOn(nameof(IsSearchBoxFocused))]
        [DependsOn(nameof(SearchBoxText))]
        public Visibility SearchPlaceholderVisibility =>
            (IsSearchBoxFocused == false && string.IsNullOrWhiteSpace(SearchBoxText)).ToVisibility();

        public string FilterResultsInfo => null;

        public ListBoxWindowViewModel()
        {
            AllItems = new ObservableCollection<object>();
            FilteredItems = new ObservableCollection<object>();
        }

        public void RefreshFilteredItems()
        {
            var searchTextToLowerInvariant = SearchBoxText?.ToLowerInvariant();
            FilteredItems.Clear();

            foreach (var item in AllItems)
            {
                var shouldInclude =
                    string.IsNullOrWhiteSpace(searchTextToLowerInvariant) ||
                    item.ToString().ToLowerInvariant().Contains(searchTextToLowerInvariant);
                if (shouldInclude)
                {
                    FilteredItems.Add(item);
                }
            }
        }
    }
}
