using Prism.Commands;
using Prism.Mvvm;
using Prism.Navigation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xamarin.Essentials;

namespace PhxAccessExample.ViewModels
{
    public class AboutPageViewModel : ViewModelBase
    {

        public DelegateCommand DiscoverCommand { get; }

        public DelegateCommand OpenLDARToolsCommand { get; }

        public DelegateCommand OpenProjectCommand { get; }


        public AboutPageViewModel(INavigationService navigationService)
            : base(navigationService)
        {
            Title = "About this app";

            DiscoverCommand = new DelegateCommand(async () =>
                {
                    try
                    {
                        var result = await NavigationService.NavigateAsync("DiscoverPage");

                        if (!result.Success)
                        {

                        }

                    }
                    catch (Exception ex)
                    {

                        throw;
                    }
                });

            OpenLDARToolsCommand = new DelegateCommand(async () => await Browser.OpenAsync("http://www.ldartools.com/"));
            OpenProjectCommand = new DelegateCommand(async () => await Browser.OpenAsync("https://github.com/ngableldar/PhxAccess"));
        }
    }
}
