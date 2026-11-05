using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace VnManager.PlaynitePlugin.Installation;

public partial class PortableDestinationView : UserControl, INotifyPropertyChanged
{
    private string purpose;
    private string destinationDirectory;
    private string validationError;

    public PortableDestinationView()
    {
        InitializeComponent();
        DataContext = this;
    }

    public event PropertyChangedEventHandler PropertyChanged;
    public event EventHandler BrowseDestinationRequested;
    public event EventHandler ContinueRequested;
    public event EventHandler CancelRequested;

    public string Purpose { get => purpose; set => SetField(ref purpose, value); }
    public string DestinationDirectory { get => destinationDirectory; set => SetField(ref destinationDirectory, value); }
    public string ValidationError { get => validationError; set => SetField(ref validationError, value); }

    private void BrowseDestinationClick(object sender, RoutedEventArgs e) => BrowseDestinationRequested?.Invoke(this, EventArgs.Empty);
    private void ContinueClick(object sender, RoutedEventArgs e) => ContinueRequested?.Invoke(this, EventArgs.Empty);
    private void CancelClick(object sender, RoutedEventArgs e) => CancelRequested?.Invoke(this, EventArgs.Empty);

    private void SetField(ref string field, string value, [CallerMemberName] string propertyName = null)
    {
        if (string.Equals(field, value, StringComparison.Ordinal)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
