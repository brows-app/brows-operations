using Brows.Composition;
using Brows.Operations;
using System.Windows;

namespace Brows;

/// <summary>Initializes composition and opens the operations demonstration window.</summary>
sealed partial class OperationsSampleApp : IImportEnvironment {
    /// <summary>Initializes imported services and displays the operations demonstration window.</summary>
    /// <param name="e">The application's startup event arguments.</param>
    protected sealed override async void OnStartup(StartupEventArgs e) {
        base.OnStartup(e);
        var imported = await Imports.Init(this, default);
        try {
            MainWindow = imported.Find<OperationsSampleWindow>();
            MainWindow.ShowDialog();
        }
        finally {
            imported.Kill();
        }
    }

    ImportInfo IImportEnvironment.ImportInfo => ImportInfo.Listed(
        list: () => [
            new OperatorFactory(),
            new OperationsSampleWindow(),
        ],
        variables: () => new());

}

