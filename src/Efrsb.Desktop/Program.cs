using System.Net.Http.Headers;
using System.Net.Http.Json;
using Efrsb.Contracts.Auth;
using Efrsb.Contracts.Companies;

namespace Efrsb.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

public sealed class MainForm : Form
{
    private readonly HttpClient _http = new() { BaseAddress = new Uri("https://localhost:5001") };
    private readonly TextBox _email = new() { PlaceholderText = "Email", Width = 260 };
    private readonly TextBox _password = new() { PlaceholderText = "Пароль", PasswordChar = '*', Width = 260 };
    private readonly TextBox _query = new() { PlaceholderText = "ИНН / ОГРН / название / GUID", Width = 360 };
    private readonly ListBox _companies = new() { Width = 620, Height = 280 };

    public MainForm()
    {
        Text = "ЕФРСБ V2 Desktop Client";
        Width = 720;
        Height = 520;

        var login = new Button { Text = "Войти", Width = 120 };
        login.Click += async (_, _) => await LoginAsync();

        var add = new Button { Text = "Добавить компанию", Width = 160 };
        add.Click += async (_, _) => await AddCompanyAsync();

        var refresh = new Button { Text = "Обновить", Width = 120 };
        refresh.Click += async (_, _) => await LoadCompaniesAsync();

        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), AutoScroll = true };
        panel.Controls.AddRange(new Control[] { _email, _password, login, _query, add, refresh, _companies });
        Controls.Add(panel);
    }

    private async Task LoginAsync()
    {
        var response = await _http.PostAsJsonAsync("/api/auth/login", new LoginRequest(_email.Text, _password.Text));
        if (!response.IsSuccessStatusCode) { MessageBox.Show("Ошибка входа"); return; }
        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>();
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.AccessToken);
        await LoadCompaniesAsync();
    }

    private async Task AddCompanyAsync()
    {
        var response = await _http.PostAsJsonAsync("/api/companies", new CreateTrackedCompanyRequest(_query.Text));
        if (!response.IsSuccessStatusCode) { MessageBox.Show(await response.Content.ReadAsStringAsync()); return; }
        _query.Clear();
        await LoadCompaniesAsync();
    }

    private async Task LoadCompaniesAsync()
    {
        var companies = await _http.GetFromJsonAsync<List<TrackedCompanyDto>>("/api/companies");
        _companies.Items.Clear();
        foreach (var company in companies ?? [])
            _companies.Items.Add($"{company.Name ?? company.SearchQuery} | unread: {company.UnreadCount} | {company.BankruptGuid}");
    }
}
