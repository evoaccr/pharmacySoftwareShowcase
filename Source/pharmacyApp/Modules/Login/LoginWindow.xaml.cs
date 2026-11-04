using System.Windows;
using pharmacyApp.Infrastructure;

namespace pharmacyApp.Modules.Login;

public partial class LoginWindow : Window
{
    private readonly UserSessionRepository repository;

    public LoginWindow(UserSessionRepository repository)
    {
        this.repository = repository;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public LoginUser? AuthenticatedUser { get; private set; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await LoadUsersAsync();
    }

    private async Task LoadUsersAsync()
    {
        SetBusy(true);
        try
        {
            var users = await repository.LoadActiveUsersAsync();
            UserBox.ItemsSource = users;
            UserBox.SelectedIndex = users.Count > 0 ? 0 : -1;
            StatusText.Text = users.Count == 0 ? "Aktif kullanıcı bulunamadı." : string.Empty;
            if (users.Count > 0) PasswordEntry.Focus();
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Kullanıcılar yüklenemedi: {exception.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void LoginClick(object sender, RoutedEventArgs e)
    {
        if (UserBox.SelectedItem is not LoginUser user)
        {
            StatusText.Text = "Bir kullanıcı seçin.";
            return;
        }

        SetBusy(true);
        StatusText.Text = string.Empty;
        try
        {
            var authenticated = await repository.AuthenticateAsync(user.UserId, PasswordEntry.Password);
            if (authenticated is null)
            {
                StatusText.Text = "Parola yanlış.";
                PasswordEntry.SelectAll();
                PasswordEntry.Focus();
                return;
            }

            AuthenticatedUser = authenticated;
            DialogResult = true;
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Giriş yapılamadı: {exception.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        LoginButton.IsEnabled = !busy;
        UserBox.IsEnabled = !busy;
        PasswordEntry.IsEnabled = !busy;
    }
}
