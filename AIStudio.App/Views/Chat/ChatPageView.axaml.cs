using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AIStudio.App.ViewModels.Chat;

namespace AIStudio.App.Views.Chat;

public partial class ChatPageView : UserControl
{
    private ChatPageViewModel?    _vm;
    private ConversationViewModel? _subscribedConv;
    private bool                   _autoScroll = true;

    /// <summary>
    /// True když právě programmaticky scrollujeme na konec (uvnitř ScrollToBottom retry
    /// okna). OnScrollChanged tehdy NESMÍ přepočítat _autoScroll, protože by ho mohl
    /// chybně vypnout uprostřed sekvence (intermediate layout state kde atBottom != true).
    /// </summary>
    private bool                   _programmaticScroll;

    public ChatPageView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += (_, _) => FocusInputBox();
        Unloaded += OnUnloaded;

        var scroll = this.FindControl<ScrollViewer>("MessagesScroll")!;
        scroll.ScrollChanged += OnScrollChanged;
    }

    private void OnUnloaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm = null;
        }

        if (_subscribedConv is not null)
        {
            _subscribedConv.Messages.CollectionChanged -= OnMessagesChanged;
            foreach (var m in _subscribedConv.Messages)
                m.PropertyChanged -= OnMessagePropertyChanged;
            _subscribedConv = null;
        }
    }

    /// <summary>
    /// Posune fokus do pole pro psaní zprávy. Jinak by uživatel musel napřed
    /// kliknout do textboxu, aby Enter poslal zprávu — první stisk se ztrácel.
    /// Voláno po startu a po každé změně konverzace.
    /// </summary>
    private void FocusInputBox()
    {
        Dispatcher.UIThread.Post(() =>
        {
            this.FindControl<TextBox>("ChatInputBox")?.Focus();
        }, DispatcherPriority.Loaded);
    }

    // ── DataContext wiring ────────────────────────────────────────────────────

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
            _vm.PropertyChanged -= OnVmPropertyChanged;

        _vm = DataContext as ChatPageViewModel;

        if (_vm is not null)
            _vm.PropertyChanged += OnVmPropertyChanged;

        SetAutoScroll(true);
        ScrollToBottom();
        FocusInputBox();
    }

    // ── FAB (scroll-to-bottom) ────────────────────────────────────────────────

    private void SetAutoScroll(bool value)
    {
        _autoScroll = value;
        var fab = this.FindControl<Button>("ScrollToBottomFab");
        if (fab is not null) fab.IsVisible = !value;
    }

    private void ScrollToBottomFab_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        SetAutoScroll(true);
        ScrollToBottom();
    }

    // ── Attachment preview ────────────────────────────────────────────────────

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatPageViewModel.AttachedImagePath))
        {
            UpdateAttachmentPreview();
            return;
        }

        if (e.PropertyName != nameof(ChatPageViewModel.SelectedConversation)) return;

        // Přepnout CollectionChanged + per-message PropertyChanged odběr na novou konverzaci
        if (_subscribedConv is not null)
        {
            _subscribedConv.Messages.CollectionChanged -= OnMessagesChanged;
            foreach (var m in _subscribedConv.Messages)
                m.PropertyChanged -= OnMessagePropertyChanged;
        }

        _subscribedConv = _vm?.SelectedConversation;

        if (_subscribedConv is not null)
        {
            _subscribedConv.Messages.CollectionChanged += OnMessagesChanged;
            // Pokrývá i regenerate/edit existující asistentské zprávy — přechod
            // IsStreaming false→true→false znovu vyvolá auto-scroll
            foreach (var m in _subscribedConv.Messages)
                m.PropertyChanged += OnMessagePropertyChanged;
        }

        SetAutoScroll(true);
        ScrollToBottom();
        FocusInputBox();
    }

    private void UpdateAttachmentPreview()
    {
        var preview = this.FindControl<Image>("AttachmentPreview");
        if (preview is null || _vm is null) return;

        var path = _vm.AttachedImagePath;
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            try
            {
                using var stream = File.OpenRead(path);
                preview.Source = new Bitmap(stream);
            }
            catch { preview.Source = null; }
        }
        else
        {
            preview.Source = null;
        }
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Přihlas se / odhlas se z PropertyChanged jednotlivých zpráv —
        // potřebujeme zachytit přepnutí IsStreaming → false (dokončení odpovědi),
        // kdy se mění výška bubliny (plain text → markdown viewer) a default
        // auto-scroll based on ExtentDelta to nemusí stihnout pokrýt.
        if (e.NewItems is not null)
            foreach (ChatMessage m in e.NewItems)
                m.PropertyChanged += OnMessagePropertyChanged;

        if (e.OldItems is not null)
            foreach (ChatMessage m in e.OldItems)
                m.PropertyChanged -= OnMessagePropertyChanged;

        // Nová zpráva přidána → scrolluj pokud jsme dole
        if (e.Action == NotifyCollectionChangedAction.Add && _autoScroll)
            ScrollToBottom();
    }

    private void OnMessagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Po dokončení streamu (IsStreaming přejde na false) layout přepočítá
        // bublinu s markdown rendererem — explicitně doscrollujeme na konec,
        // jinak by poslední řádky bubliny mohly viset pod viewportem.
        if (e.PropertyName == nameof(ChatMessage.IsStreaming) &&
            sender is ChatMessage { IsStreaming: false } &&
            _autoScroll)
        {
            ScrollToBottom();
        }
    }

    // ── Auto-scroll ───────────────────────────────────────────────────────────

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        var scroll = (ScrollViewer)sender!;

        if (e.ExtentDelta.Y > 0)
        {
            // Obsah se zvětšil (streaming) — scrollovat pokud jsme dole
            if (_autoScroll) ScrollToBottom();
            return;
        }

        if (e.OffsetDelta.Y != 0)
        {
            // Během programmatic scroll (ScrollToBottom retry okno) NESMÍME
            // přepočítávat _autoScroll — intermediate layout stavy mohou hlásit
            // atBottom=false a tím by se vypnul auto-scroll uprostřed sekvence.
            if (_programmaticScroll) return;

            // Uživatel scrolloval — zjisti, jestli je na konci
            // B6: relativní práh — min 40 px nebo 10 % výšky viewportu (pro krátké/vysoké okno)
            var threshold = Math.Max(40, scroll.Viewport.Height * 0.1);
            var atBottom  = scroll.Offset.Y + scroll.Viewport.Height
                            >= scroll.Extent.Height - threshold;
            SetAutoScroll(atBottom);
        }
    }

    private void ScrollToBottom()
    {
        // Auto-scroll na úplný konec poslední bubliny. Robust proti:
        //   • Conversation switch (mnoho bublin se postupně měří přes MarkdownViewer)
        //   • IsStreaming → false transition (action buttons row se objeví AŽ POTOM)
        //   • MarkdownViewer s code blocks / headings (deferred layout)
        //
        // Strategie: BEZPODMÍNĚČNĚ re-scroll po každém layout pass po dobu
        // ~25 framů (~400 ms při 60 fps). Předchozí verze re-scrollovala jen
        // při růstu Extent — pokud první ScrollToEnd nedosáhl skutečný konec
        // (např. action row ještě nebyl rendrován, ale Extent byl spočítán až
        // do něj), retry se nikdy nespustil.

        var scroll = this.FindControl<ScrollViewer>("MessagesScroll");
        if (scroll is null) return;

        // Vstupujeme do programmatic scroll okna — OnScrollChanged nesmí měnit _autoScroll
        // (intermediate layout pass může chybně reportovat atBottom=false).
        _programmaticScroll = true;

        Dispatcher.UIThread.Post(() =>
        {
            scroll.ScrollToEnd();

            var framesSeen = 0;
            EventHandler? handler = null;
            handler = (_, _) =>
            {
                framesSeen++;
                // Re-scroll BEZPODMÍNĚČNĚ — _autoScroll guard tu být nesmí, protože by
                // se vypnul ze stejného důvodu, proč jsme nastavili _programmaticScroll.
                scroll.ScrollToEnd();
                // Safety brake: po 25 frames unsubscribe + uvolnit programmatic flag
                if (framesSeen >= 25)
                {
                    scroll.LayoutUpdated -= handler!;
                    _programmaticScroll = false;
                }
            };
            scroll.LayoutUpdated += handler;
        }, DispatcherPriority.Render);
    }

    // ── Title inline edit ─────────────────────────────────────────────────────

    private void TitleEditBox_LostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is ChatPageViewModel vm)
            vm.ConfirmTitleEditCommand.Execute(null);
    }

    private void TitleEditBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ChatPageViewModel vm) return;

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            vm.ConfirmTitleEditCommand.Execute(null);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            vm.CancelTitleEditCommand.Execute(null);
        }
    }

    // ── Chat input ────────────────────────────────────────────────────────────

    private void InputBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ChatPageViewModel vm) return;

        // Enter (bez Shift) nebo Ctrl+Enter → odeslat zprávu
        var isEnter     = e.Key == Key.Enter && (e.KeyModifiers & KeyModifiers.Shift) == 0;
        var isCtrlEnter = e.Key == Key.Enter && (e.KeyModifiers & KeyModifiers.Control) != 0;

        if (isEnter || isCtrlEnter)
        {
            e.Handled = true;
            vm.SendMessageCommand.Execute(null);
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (vm.IsSending)
                vm.StopGenerationCommand.Execute(null);
            // Pokud probíhá edit zprávy, zruší ho první Esc
            else if (vm.SelectedConversation?.Messages
                         .FirstOrDefault(m => m.IsEditing) is { } editing)
                editing.CancelEditCommand.Execute(null);
        }
    }
}
