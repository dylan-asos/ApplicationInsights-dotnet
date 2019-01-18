namespace Microsoft.ApplicationInsights.W3C
{
    using System;
    using System.ComponentModel;
    using System.Diagnostics;
    using Microsoft.ApplicationInsights.Channel;
    using Microsoft.ApplicationInsights.DataContracts;
    using Microsoft.ApplicationInsights.Extensibility;
    using Microsoft.ApplicationInsights.Extensibility.Implementation;

    /// <summary>
    /// Telemetry Initializer that sets correlation ids for W3C.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public class W3COperationCorrelationTelemetryInitializer : ITelemetryInitializer
    {
        private const string RddDiagnosticSourcePrefix = "rdddsc";
        private const string SqlRemoteDependencyType = "SQL";

        /// <summary>
        /// Initializes telemetry item.
        /// </summary>
        /// <param name="telemetry">Telemetry item.</param>
        public void Initialize(ITelemetry telemetry)
        {
            Activity currentActivity = Activity.Current;
            UpdateTelemetry(telemetry, currentActivity, false);
        }

        internal static void UpdateTelemetry(ITelemetry telemetry, Activity activity, bool forceUpdate)
        {
            if (activity == null)
            {
                return;
            }

            activity.UpdateContextOnActivity();

            // Requests and dependencies are initialized from the current Activity 
            // (i.e. telemetry.Id = current.Id). Activity is created for such requests specifically
            // Traces, exceptions, events on the other side are children of current activity
            // There is one exception - SQL DiagnosticSource where current Activity is a parent
            // for dependency calls.

            OperationTelemetry opTelemetry = telemetry as OperationTelemetry;
            bool initializeFromCurrent = opTelemetry != null;

            if (initializeFromCurrent)
            {
                initializeFromCurrent &= !(opTelemetry is DependencyTelemetry dependency &&
                                           dependency.Type == SqlRemoteDependencyType &&
                                           dependency.Context.GetInternalContext().SdkVersion
                                               .StartsWith(RddDiagnosticSourcePrefix, StringComparison.Ordinal));
            }

            string spanId = null, parentSpanId = null;
            foreach (var tag in activity.Tags)
            {
                switch (tag.Key)
                {
                    case W3CConstants.TraceIdTag:
                        telemetry.Context.Operation.Id = tag.Value;
                        break;
                    case W3CConstants.SpanIdTag:
                        spanId = tag.Value;
                        break;
                    case W3CConstants.ParentSpanIdTag:
                        parentSpanId = tag.Value;
                        break;
                    case W3CConstants.TracestateTag:
                        if (telemetry is OperationTelemetry operation)
                        {
                            operation.Properties[W3CConstants.TracestateTag] = tag.Value;
                        }

                        break;
                }
            }

            if (initializeFromCurrent)
            {
#if NET45
                // on .NET Fx Activities are not always reliable, this code prevents update
                // of the telemetry that was forcibly updated during Activity lifetime
                // ON .NET Core there is no such problem 
                // if spanId is valid already and update is not forced, ignore it
                if (!forceUpdate && IsValidTelemetryId(opTelemetry.Id, telemetry.Context.Operation.Id))
                {
                    return;
                }
#endif
                opTelemetry.Id = FormatRequestId(telemetry.Context.Operation.Id, spanId);
                if (parentSpanId != null)
                {
                    telemetry.Context.Operation.ParentId =
                        FormatRequestId(telemetry.Context.Operation.Id, parentSpanId);
                }
            }
            else
            {
                telemetry.Context.Operation.ParentId =
                    FormatRequestId(telemetry.Context.Operation.Id, spanId);
            }

            if (opTelemetry != null)
            {
                if (opTelemetry.Context.Operation.Id != activity.RootId)
                {
                    opTelemetry.Properties[W3CConstants.LegacyRootIdProperty] = activity.RootId;
                }

                if (opTelemetry.Id != activity.Id)
                {
                    opTelemetry.Properties[W3CConstants.LegacyRequestIdProperty] = activity.Id;
                }
            }
        }

#if NET45
        private static bool IsValidTelemetryId(string id, string operationId)
        {
            return id.Length == 51 &&
                   id[0] == '|' &&
                   id[33] == '.' &&
                   id.IndexOf('.', 34) == 50 &&
                   id.IndexOf(operationId, 1, 33, StringComparison.Ordinal) == 1;
        }
#endif

        private static string FormatRequestId(string traceId, string spanId)
        {
            return string.Concat("|", traceId, ".", spanId, ".");
        }
    }
}
