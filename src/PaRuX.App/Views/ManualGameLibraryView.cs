using System;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;
using Windows.Storage.Pickers;

namespace PaRuX.Views;

public sealed class ManualGameLibraryView : UserControl
{
    private readonly TextBox _name = new();
    private readonly TextBox _path = new();
    private readonly StackPanel _games = new() { Spacing = 8 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _add = new();
    private readonly Button _browse = new();
    private bool _busy;
    private readonly TextBox _filter = new();
    private List<ManualGameDto> _allGames = new();
    private int _page;
    private readonly Button _previous = new();
    private readonly Button _next = new();
    private static bool Italian => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it";
    private static string T(string en, string it) => Italian ? it : en;

    public ManualGameLibraryView()
    {
        var root = new StackPanel { Spacing = 16, Padding = new Thickness(24), MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Stretch };
        root.Children.Add(new TextBlock { Text = T("Game library", "Libreria giochi"), FontSize = 28, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        root.Children.Add(new TextBlock { Text = T("Add local games to your list. PaRuX does not launch or install them here.", "Aggiungi giochi locali alla lista. PaRuX non li avvia né li installa da questa schermata."), TextWrapping = TextWrapping.Wrap });
        _name.Header = T("Game name", "Nome del gioco");
        _name.MaxLength = 200;
        AutomationProperties.SetAutomationId(_name, "GameLibrary_Name_TextBox");
        _path.Header = T("Local executable (.exe)", "Eseguibile locale (.exe)");
        _path.MaxLength = 1024;
        AutomationProperties.SetAutomationId(_path, "GameLibrary_Path_TextBox");
        _browse.Content = T("Browse", "Sfoglia");
        AutomationProperties.SetAutomationId(_browse, "GameLibrary_Browse_Button");
        _browse.Click += Browse_Click;
        _add.Content = T("Add game", "Aggiungi gioco");
        AutomationProperties.SetAutomationId(_add, "GameLibrary_Add_Button");
        _add.Click += Add_Click;
        root.Children.Add(_name); root.Children.Add(_path);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(_browse); buttons.Children.Add(_add); root.Children.Add(buttons);
        AutomationProperties.SetAutomationId(_status, "GameLibrary_Status_Text");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        root.Children.Add(_status);
        _filter.Header = T("Filter by name", "Filtra per nome");
        AutomationProperties.SetAutomationId(_filter, "GameLibrary_Filter_TextBox");
        _filter.TextChanged += (_, _) => { _page = 0; RenderGames(); };
        root.Children.Add(_filter); root.Children.Add(_games);
        var pages = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _previous.Content = T("Previous", "Precedente"); _next.Content = T("Next", "Successiva");
        _previous.Click += (_, _) => { if (_page > 0) { _page--; RenderGames(); } };
        _next.Click += (_, _) => { _page++; RenderGames(); };
        pages.Children.Add(_previous); pages.Children.Add(_next); root.Children.Add(pages);
        Content = new ScrollViewer { Content = root, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    public async void OnShown() => await RefreshAsync();

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".exe");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin));
            var file = await picker.PickSingleFileAsync();
            if (file != null) _path.Text = file.Path;
        }
        catch (Exception) { _status.Text = T("Could not open the file picker.", "Impossibile aprire la selezione file."); }
        finally { SetBusy(false); }
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            var result = await GamingOrchestratorClient.Current.AddManualGameAsync(new AddManualGameRequest(_name.Text, _path.Text));
            if (!result.Succeeded) { _status.Text = T("Could not add the game. Check the name, local .exe path, or whether it is already listed.", "Impossibile aggiungere il gioco. Controlla nome, percorso .exe locale o eventuali duplicati."); return; }
            _name.Text = ""; _path.Text = "";
            await LoadAsync();
        }
        catch (Exception) { _status.Text = T("Could not add the game.", "Impossibile aggiungere il gioco."); }
        finally { SetBusy(false); }
    }

    private async Task RefreshAsync()
    {
        if (_busy) return;
        SetBusy(true);
        try { await LoadAsync(); }
        catch (Exception) { _status.Text = T("Could not load the library.", "Impossibile caricare la libreria."); }
        finally { SetBusy(false); }
    }

    private async Task LoadAsync()
    {
        _status.Text = T("Loading…", "Caricamento…");
        var result = await GamingOrchestratorClient.Current.ListManualGamesAsync();
        if (!result.Succeeded || result.Value == null) { _status.Text = T("Could not load the library. Try opening it again.", "Impossibile caricare la libreria. Prova a riaprirla."); return; }
        _allGames = result.Value;
        _page = 0;
        RenderGames();
    }

    private void RenderGames()
    {
        _games.Children.Clear();
        var matches = _allGames.Where(game => game.Name.Contains(_filter.Text ?? "", StringComparison.CurrentCultureIgnoreCase)).ToList();
        if (_page * 100 >= matches.Count) _page = Math.Max(0, (matches.Count - 1) / 100);
        int first = matches.Count == 0 ? 0 : _page * 100 + 1;
        int last = Math.Min(matches.Count, (_page + 1) * 100);
        _status.Text = matches.Count == 0 ? T("No matching games.", "Nessun gioco corrispondente.") :
            T($"Showing {first}–{last} of {matches.Count}.", $"Mostrati {first}–{last} di {matches.Count}.");
        _previous.IsEnabled = _page > 0; _next.IsEnabled = last < matches.Count;
        foreach (ManualGameDto game in matches.Skip(_page * 100).Take(100))
        {
            var card = new StackPanel { Spacing = 4, Padding = new Thickness(12) };
            card.Children.Add(new TextBlock { Text = game.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            card.Children.Add(new TextBlock { Text = game.ExecutablePath, TextWrapping = TextWrapping.Wrap });
            var remove = new Button { Content = T("Remove from list", "Rimuovi dalla lista"), HorizontalAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetName(remove, T("Remove ", "Rimuovi ") + game.Name);
            AutomationProperties.SetAutomationId(remove, "GameLibrary_Remove_" + game.Id);
            remove.Click += async (_, _) => await RemoveAsync(game);
            card.Children.Add(remove); _games.Children.Add(card);
        }
    }

    private async Task RemoveAsync(ManualGameDto game)
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = T("Remove game?", "Rimuovere il gioco?"), Content = T("This removes only the entry; game files stay on disk.", "Viene rimossa solo la voce; i file del gioco restano sul disco."), PrimaryButtonText = T("Remove", "Rimuovi"), CloseButtonText = T("Cancel", "Annulla"), DefaultButton = ContentDialogButton.Close };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var result = await GamingOrchestratorClient.Current.RemoveManualGameAsync(game.Id);
            if (!result.Succeeded) { _status.Text = T("Could not remove the entry.", "Impossibile rimuovere la voce."); return; }
            await LoadAsync();
        }
        catch (Exception) { _status.Text = T("Could not remove the entry.", "Impossibile rimuovere la voce."); }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool value) { _busy = value; _add.IsEnabled = !value; _browse.IsEnabled = !value; _games.IsHitTestVisible = !value; }
}
