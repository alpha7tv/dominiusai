using Avalonia.Controls;
using Avalonia.Controls.Templates;
using DominiusPlay.Desktop.ViewModels;
using DominiusPlay.Desktop.Views;

namespace DominiusPlay.Desktop;

/// <summary>Associa cada ViewModel à sua tela.</summary>
public class ViewLocator : IDataTemplate
{
    public Control? Build(object? data) => data switch
    {
        LoginViewModel => new LoginView(),
        DashboardViewModel => new DashboardView(),
        ConversationsViewModel => new ConversationsView(),
        KanbanViewModel => new KanbanView(),
        LeadsViewModel => new LeadsView(),
        PlansViewModel => new PlansView(),
        KnowledgeViewModel => new KnowledgeView(),
        TemplatesViewModel => new TemplatesView(),
        IntegrationsViewModel => new IntegrationsView(),
        AiSettingsViewModel => new AiSettingsView(),
        CampaignsViewModel => new CampaignsView(),
        ReportsViewModel => new ReportsView(),
        TeamViewModel => new TeamView(),
        PendingModuleViewModel => new PendingModuleView(),
        _ => new TextBlock { Text = "Tela não encontrada: " + data?.GetType().Name },
    };

    public bool Match(object? data) => data is ViewModelBase;
}
