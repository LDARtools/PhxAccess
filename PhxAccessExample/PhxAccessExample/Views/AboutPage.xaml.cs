using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PhxAccessExample.ViewModels;
using Xamarin.Forms;

namespace PhxAccessExample.Views
{
    public partial class AboutPage : ContentPage
    {
        public AboutPage()
        {
            InitializeComponent();
        }

        private void TapGestureRecognizer_OnTapped(object sender, EventArgs e)
        {
            (BindingContext as AboutPageViewModel)?.DiscoverCommand?.Execute();
        }
    }
}