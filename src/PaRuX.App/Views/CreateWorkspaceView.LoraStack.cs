using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PaRuX.Contracts;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>
    /// Create > Image "LoRA stack": up to three installed LoRAs, each with a strength slider. The installed list and the
    /// family check come from PaRuX Core (GET /api/v1/media/loras/installed); a LoRA made for another model family is
    /// shown but cannot be chosen, and the server refuses it again with its own reason if it is ever sent. Wording and
    /// AutomationIds live in <see cref="CreateLoraStackPresenter"/>.
    /// </summary>
    public sealed partial class CreateWorkspaceView
    {
        private readonly ComboBox[] _loraStackCombos = new ComboBox[CreateLoraStackPresenter.MaxSlots];
        private readonly Slider[] _loraStackStrengths = new Slider[CreateLoraStackPresenter.MaxSlots];
        private readonly TextBlock[] _loraStackStrengthTexts = new TextBlock[CreateLoraStackPresenter.MaxSlots];
        private readonly StackPanel[] _loraStackRows = new StackPanel[CreateLoraStackPresenter.MaxSlots];
        private readonly TextBlock _loraStackStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.8, FontSize = 12 };
        private readonly TextBlock _loraStackIntro = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.72, FontSize = 12 };
        private readonly Button _loraStackAdd = new Button { Content = "Add another LoRA" };
        private List<InstalledLoraChoice> _loraStackChoices = new();
        private int _loraStackVisibleRows = 1;
        private bool _loraStackLoading;

        private UIElement BuildLoraStackPanel()
        {
            var panel = new StackPanel { Spacing = 6 };
            AutomationProperties.SetAutomationId(panel, CreateLoraStackPresenter.Panel);
            panel.Children.Add(new TextBlock { Text = "LoRA stack (optional)", FontSize = 12, Opacity = 0.72 });
            panel.Children.Add(_loraStackIntro);

            for (int i = 0; i < CreateLoraStackPresenter.MaxSlots; i++)
            {
                int index = i;
                var combo = new ComboBox { MinWidth = 320, PlaceholderText = CreateLoraStackPresenter.NoneLabel };
                var slider = new Slider
                {
                    Minimum = CreateLoraStackPresenter.MinStrength, Maximum = CreateLoraStackPresenter.MaxStrength,
                    StepFrequency = 0.05, Value = CreateLoraStackPresenter.DefaultStrength, Width = 200
                };
                var text = new TextBlock { FontSize = 12, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center };
                var remove = new Button { Content = "Remove" };
                AutomationProperties.SetAutomationId(combo, CreateLoraStackPresenter.SlotCombo(index));
                AutomationProperties.SetName(combo, "LoRA " + (index + 1));
                AutomationProperties.SetAutomationId(slider, CreateLoraStackPresenter.SlotStrength(index));
                AutomationProperties.SetName(slider, "LoRA " + (index + 1) + " strength");
                AutomationProperties.SetAutomationId(text, CreateLoraStackPresenter.SlotStrengthText(index));
                AutomationProperties.SetAutomationId(remove, CreateLoraStackPresenter.SlotRemove(index));
                slider.ValueChanged += (_, _) => { text.Text = CreateLoraStackPresenter.StrengthText(slider.Value); UpdateLoraStackStatus(); };
                combo.SelectionChanged += (_, _) => UpdateLoraStackStatus();
                remove.Click += (_, _) => RemoveLoraStackSlot(index);
                text.Text = CreateLoraStackPresenter.StrengthText(slider.Value);

                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                row.Children.Add(combo);
                row.Children.Add(slider);
                row.Children.Add(text);
                row.Children.Add(remove);
                _loraStackCombos[i] = combo;
                _loraStackStrengths[i] = slider;
                _loraStackStrengthTexts[i] = text;
                _loraStackRows[i] = row;
                panel.Children.Add(row);
            }

            AutomationProperties.SetAutomationId(_loraStackAdd, CreateLoraStackPresenter.AddButton);
            _loraStackAdd.Click += (_, _) =>
            {
                if (_loraStackVisibleRows < CreateLoraStackPresenter.MaxSlots) _loraStackVisibleRows++;
                ApplyLoraStackRowVisibility();
            };
            var refresh = new Button { Content = "Refresh LoRAs" };
            AutomationProperties.SetAutomationId(refresh, CreateLoraStackPresenter.RefreshButton);
            refresh.Click += async (_, _) => await RefreshLoraStackAsync();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            buttons.Children.Add(_loraStackAdd);
            buttons.Children.Add(refresh);
            panel.Children.Add(buttons);
            AutomationProperties.SetAutomationId(_loraStackStatus, CreateLoraStackPresenter.Status);
            panel.Children.Add(_loraStackStatus);

            ApplyLoraStackRowVisibility();
            Loaded += async (_, _) => await RefreshLoraStackAsync();
            return panel;
        }

        private void ApplyLoraStackRowVisibility()
        {
            for (int i = 0; i < _loraStackRows.Length; i++)
                _loraStackRows[i].Visibility = i < _loraStackVisibleRows ? Visibility.Visible : Visibility.Collapsed;
            _loraStackAdd.Visibility = _loraStackVisibleRows < CreateLoraStackPresenter.MaxSlots ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RemoveLoraStackSlot(int index)
        {
            _loraStackCombos[index].SelectedIndex = 0;
            _loraStackStrengths[index].Value = CreateLoraStackPresenter.DefaultStrength;
            if (index > 0 && index == _loraStackVisibleRows - 1) _loraStackVisibleRows--;
            ApplyLoraStackRowVisibility();
            UpdateLoraStackStatus();
        }

        private List<LoraStackSlot> CurrentLoraSlots() =>
            Enumerable.Range(0, _loraStackCombos.Length)
                .Where(i => i < _loraStackVisibleRows)
                .Select(i => new LoraStackSlot((_loraStackCombos[i].SelectedItem as ComboBoxItem)?.Tag as string, _loraStackStrengths[i].Value))
                .ToList();

        private void UpdateLoraStackStatus() =>
            _loraStackStatus.Text = _loraStackChoices.Count == 0 && CurrentLoraSlots().All(s => s.Name == null)
                ? CreateLoraStackPresenter.EmptyList
                : CreateLoraStackPresenter.Summary(CurrentLoraSlots(), _loraStackChoices);

        private async Task RefreshLoraStackAsync()
        {
            if (_loraStackLoading) return;
            _loraStackLoading = true;
            try
            {
                var previous = CurrentLoraSlots().Select(s => s.Name).ToList();
                var (ok, body) = await SendAsync(HttpMethod.Get, "/api/v1/media/loras/installed", null, CancellationToken.None);
                if (!ok)
                {
                    _loraStackStatus.Text = "The LoRA list could not be read from PaRuX Core. " + ErrorOf(body, "Try Refresh LoRAs again.");
                    return;
                }
                var (family, choices) = CreateLoraStackPresenter.ParseInstalled(body);
                _loraStackChoices = choices;
                _loraStackIntro.Text = CreateLoraStackPresenter.Intro(family);
                for (int i = 0; i < _loraStackCombos.Length; i++)
                {
                    ComboBox combo = _loraStackCombos[i];
                    combo.Items.Clear();
                    combo.Items.Add(new ComboBoxItem { Content = CreateLoraStackPresenter.NoneLabel, Tag = null });
                    foreach (InstalledLoraChoice choice in choices)
                        combo.Items.Add(new ComboBoxItem
                        {
                            Content = CreateLoraStackPresenter.ChoiceLabel(choice), Tag = choice.Name,
                            IsEnabled = choice.Compatibility != "incompatible"
                        });
                    string? keep = i < previous.Count ? previous[i] : null;
                    combo.SelectedIndex = Math.Max(0, combo.Items.OfType<ComboBoxItem>().ToList().FindIndex(c => (c.Tag as string) == keep && c.IsEnabled));
                }
                UpdateLoraStackStatus();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _loraStackStatus.Text = "The LoRA list is unavailable: " + ex.Message;
            }
            finally { _loraStackLoading = false; }
        }

        /// <summary>Adds the chosen LoRAs to an image or inpaint request, or throws the reason the stack cannot run.</summary>
        private void ApplyLoraStack(MediaJobRequest request)
        {
            List<LoraStackSlot> slots = CurrentLoraSlots();
            string? problem = CreateLoraStackPresenter.Problem(slots, _loraStackChoices);
            if (problem != null) throw new InvalidOperationException(problem);
            request.Loras = CreateLoraStackPresenter.BuildSelections(slots);
        }
    }
}
