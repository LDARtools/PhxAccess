using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PhxAccessExample.ViewModels;
using Xamarin.Forms;
using Xamarin.Forms.Xaml;

namespace PhxAccessExample.Views
{
    [XamlCompilation(XamlCompilationOptions.Compile)]
    public partial class Phx42DetailsPage : ContentPage
    {
        public Phx42DetailsPage()
        {
            InitializeComponent();
        }

        private void TapGestureRecognizer_OnTapped(object sender, EventArgs e)
        {
            var command = (BindingContext as Phx42DetailsPageViewModel)?.IgniteCommand;

            if (command != null && command.CanExecute())
            {
                command.Execute();
            }
        }
    }
}