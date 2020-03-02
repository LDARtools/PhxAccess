using System;
using System.Collections.Generic;
using System.Text;
using PhxAccessExample.ViewModels;
using Xamarin.Forms;

namespace PhxAccessExample.Views
{
    public class DeviceTypeTemplateSelector : DataTemplateSelector
    {
        public DataTemplate UnknownTemplate { get; set; }
        public DataTemplate Phx42Template { get; set; }
        public DataTemplate Phx21Template { get; set; }

        protected override DataTemplate OnSelectTemplate(object item, BindableObject container)
        {
            var d = item as DeviceListItemViewModel;

            if (d?.DeviceType == DeviceType.Phx42)
            {
                return Phx42Template;
            }

            if (d?.DeviceType == DeviceType.Phx21)
            {
                return Phx21Template;
            }

            return UnknownTemplate;
        }
    }
}
