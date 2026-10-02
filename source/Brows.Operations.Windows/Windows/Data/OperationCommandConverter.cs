using Brows.Operations;
using Domore.Logs;
using System;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;

namespace Brows.Windows.Data;

internal sealed class OperationCommandConverter : IValueConverter {
    private static readonly ILog Log = Logging.For(typeof(OperationCommandConverter));

    object IValueConverter.Convert(object value, Type targetType, object parameter, CultureInfo culture) {
        if (value is Operation operation) {
            return parameter?.ToString()?.ToLowerInvariant() switch {
                "cancel" => new CancelCommand(operation),
                "remove" => new RemoveCommand(operation),
                _ => throw new ArgumentException(paramName: nameof(parameter), message: "Invalid parameter")
            };
        }
        return DependencyProperty.UnsetValue;
    }

    object IValueConverter.ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) {
        throw new NotSupportedException();
    }

    private abstract class OperationCommand : ICommand {
        protected abstract bool CanExecute { get; }

        protected abstract void Execute();

        protected virtual void OnCanExecuteChanged(EventArgs e) {
            CanExecuteChanged?.Invoke(this, e);
        }

        public event EventHandler CanExecuteChanged;

        bool ICommand.CanExecute(object parameter) {
            return CanExecute;
        }

        void ICommand.Execute(object parameter) {
            Execute();
        }
    }

    private sealed class CancelCommand : OperationCommand {
        private void Operation_CanCancelChanged(object sender, EventArgs e) {
            OnCanExecuteChanged(e);
        }

        protected sealed override bool CanExecute => Operation.CanCancel;

        protected sealed override void Execute() {
            try {
                Operation.Cancel();
            }
            catch (AggregateException ex) {
                if (Log.Warn()) {
                    Log.Warn(ex);
                }
            }
        }

        public Operation Operation { get; }

        public CancelCommand(Operation operation) {
            Operation = operation ?? throw new ArgumentNullException(nameof(operation));
            WeakEventManager<Operation, EventArgs>
                .AddHandler(Operation, nameof(Operation.CanCancelChanged), Operation_CanCancelChanged);
        }
    }

    private sealed class RemoveCommand : OperationCommand {
        private void Operation_CanRemoveChanged(object sender, EventArgs e) {
            OnCanExecuteChanged(e);
        }

        protected sealed override bool CanExecute => Operation.CanRemove;

        protected sealed override void Execute() {
            Operation.Remove();
        }

        public Operation Operation { get; }

        public RemoveCommand(Operation operation) {
            Operation = operation ?? throw new ArgumentNullException(nameof(operation));
            WeakEventManager<Operation, EventArgs>
                .AddHandler(Operation, nameof(Operation.CanRemoveChanged), Operation_CanRemoveChanged);
        }
    }
}
