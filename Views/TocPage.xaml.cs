using BookViewer.Models;
using System.Collections.Generic;
using Microsoft.Maui.Controls;

namespace BookViewer.Views
{
    public partial class TocPage : ContentPage
    {
        private readonly string _bookFolder;
        private readonly BookData _bookData;

        public TocPage(string bookFolder, BookData bookData)
        {
            InitializeComponent();
            NavigationPage.SetHasNavigationBar(this, false);
            _bookFolder = bookFolder;
            _bookData = bookData;

            // Book title goes in the nav bar (centered), matching UnitPage
            TitleLabel.Text = bookData.Title ?? "";

            // "Contents" is now the section header above the unit list
            SectionHeaderLabel.Text = "Contents";

            var units = new List<UnitData>(bookData.Units);
            UnitsCollection.ItemsSource = units;
        }

        private async void OnBackTapped(object sender, System.EventArgs e)
        {
            await Navigation.PopAsync();
        }

        private async void OnUnitTapped(object sender, System.EventArgs e)
        {
            if (((Grid)sender).BindingContext is UnitData unit)
            {
                var unitPage = new UnitPage(_bookFolder, _bookData, unit);
                await Navigation.PushAsync(unitPage);
            }
        }

        private void OnUnitSelected(object sender, SelectionChangedEventArgs e)
        {
            // handled by tap gesture
        }
    }
}
