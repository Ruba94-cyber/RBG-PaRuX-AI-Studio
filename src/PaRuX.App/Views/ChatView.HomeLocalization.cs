using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace PaRuX.Views;

public sealed partial class ChatView
{
    private void EmptyState_Loaded(object sender, RoutedEventArgs e)
    {
        bool it = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it";
        HomeHeadline.Text = it ? "Chiedi qualcosa a PaRuX" : "Ask PaRuX anything";
        HomeDescription.Text = it ? "Inizia una conversazione con PaRuX." : "Start a conversation with PaRuX.";
        SetHomeSuggestion(HomeCodeSuggestion, HomeCodeLabel,
            it ? "Migliora questo progetto" : "Improve this codebase",
            it ? "Migliora il codice di questo progetto:" : "Improve the code in this project:");
        SetHomeSuggestion(HomeAnalysisSuggestion, HomeAnalysisLabel,
            it ? "Analizza un problema in profondità" : "Analyse a problem in depth",
            it ? "Analizza questo problema in profondità:" : "Analyse this problem in depth:");
        SetHomeSuggestion(HomeSummarySuggestion, HomeSummaryLabel,
            it ? "Riassumi un testo nei punti principali" : "Summarise a text into key points",
            it ? "Riassumi questo testo nei punti principali:" : "Summarise this into key points:");
        SetHomeSuggestion(HomeImageSuggestion, HomeImageLabel,
            it ? "Genera un'immagine" : "Generate an image",
            it ? "Genera un'immagine di un paesaggio futuristico, senza testo" : "Generate an image of a futuristic landscape, no text");
    }

    private static void SetHomeSuggestion(Button button, TextBlock label, string title, string prompt)
    {
        label.Text = title;
        button.Tag = prompt;
        AutomationProperties.SetName(button, title);
    }
}
