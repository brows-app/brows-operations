# Operations WPF sample

![Styled operation hierarchy with live progress, cancellation, and an intentional error.](Preview.png)

Run on Windows with .NET 10:

```powershell
dotnet run --project samples/Brows.Operations.Sample
```

**Run showcase** starts nested work, a cancellable task, and an intentional child failure together. The individual scenario buttons demonstrate parallel children, explicit progress labels, successful completion, and a parent that fails while its child is still working. Work lasts long enough to inspect the hierarchy and use Cancel. Successful operations disappear automatically; failures remain until removed. **Clear finished** calls `IOperationCollection.RemoveComplete()` to remove completed roots. Pass `withError: true` or `withError: false` to remove only failed or successful roots, respectively.

## Styling the control

[Resources/OperationsTheme.xaml](Resources/OperationsTheme.xaml) is the reusable styling example. It merges the Windows package's `Resources/Styles.xaml` dictionary and defines `ShowcaseOperatorStyle`, based on the package's implicit `OperatorControl` style. Apply it to an operator control:

```xml
<brows:OperatorControl Operator="{Binding Operator}"
                       Style="{StaticResource ShowcaseOperatorStyle}" />
```

The window uses the package's XAML namespace, `http://schemas.brows.app/winfx/2026/xaml/presentation`. Its operator is created through the imported `IOperatorFactory`. The sample calls `Operate` on the WPF dispatcher, capturing its synchronization context for root and child observable collection changes. Scenario work and progress reports also stay on the dispatcher in this sample; the library allows them on worker threads and does not dispatch property-change notifications. State updates across an operation tree must still be serialized.

| Property | Template data context / purpose |
| --- | --- |
| `OperationItemsTemplate` | Root or child collection source. Bind `ItemsSource` to `{Binding}`. |
| `Operation*HeaderTemplate` | Column header text; the depth header receives an empty string. |
| `OperationDepthContentTemplate` | Depth display string. |
| `OperationNameContentTemplate` | Operation name. |
| `OperationDataContentTemplate` | Detail text. |
| `OperationProgressContentTemplate` | Progress display string. |
| `OperationTargetContentTemplate` | Target display string. |
| `OperationErrorTemplate` | The exception itself; bind to `Message`. |
| `OperationProgressBarStyle` | Progress bar appearance and control template. |
| `OperationScrollViewerStyle` | The outer scroll viewer's appearance and scrolling settings. |
| `OperationCancelButtonStyle` | The running root's Cancel button style, including its content. |
| `OperationRemoveButtonStyle` | The finished root's Remove button style, including its content. |

The collection template uses a `ListBox` with a custom item container. It leaves `ItemTemplate` unset so the package's operation rows and child hierarchy are retained. Its control template contains only an `ItemsPresenter`, avoiding nested scroll viewers and keeping the columns aligned. The operator control owns scrolling. Error row backgrounds use `IOperation.CompleteWithError`, and the activity count uses `IOperationCollection.Count`.

The progress bar template retains WPF's `PART_Track` and `PART_Indicator` elements. A `Canvas` prevents the indicator's calculated width from inflating the desired width during horizontal scrolling.

Cancel and Remove use separate styles assigned through `OperationCancelButtonStyle` and `OperationRemoveButtonStyle`. Both target `Button` and share `OperationButtonStyle` for sizing. Remove uses the error palette. The custom styles explicitly set `Content` to supply their labels; the package owns their commands. Leaving either property unset or setting it to null uses the package's corresponding default button style and label.

The package currently fixes column widths and ordering. The sample accommodates these with bounded cell text, tooltips, and horizontal scrolling. A column sizing API would allow a more responsive layout. Template properties for the progress/error and command headers would make those areas easier to customize directly.
