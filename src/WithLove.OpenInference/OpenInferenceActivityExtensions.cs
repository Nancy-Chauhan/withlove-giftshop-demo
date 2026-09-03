using System.Diagnostics;

namespace WithLove.OpenInference;

/// <summary>Internal Activity plumbing used by the application-focused OpenInference scopes.</summary>
internal static class OpenInferenceActivityExtensions
{
    internal static Activity? StartOpenInferenceActivity(
        this ActivitySource source,
        string name,
        OpenInferenceSpanKind spanKind,
        IEnumerable<KeyValuePair<string, object?>>? initialTags,
        OpenInferenceTraceConfig traceConfig)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(traceConfig);

        if (!source.HasListeners())
        {
            return null;
        }

        var tags = new ActivityTagsCollection();
        AddAmbientTags(tags);
        if (initialTags is not null)
        {
            foreach (var tag in initialTags)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(tag.Key);
                AddTag(tags, tag.Key, tag.Value, traceConfig);
            }
        }

        tags[OpenInferenceAttributes.OpenInferenceSpanKind] = spanKind.ToAttributeValue();
        var parentContext = Activity.Current?.Context ?? default;
        return source.StartActivity(name, ActivityKind.Internal, parentContext, tags);
    }

    internal static Activity? SetOpenInferenceTag(
        this Activity? activity,
        string key,
        string value,
        OpenInferenceTraceConfig traceConfig) =>
        SetAttribute(activity, key, value, traceConfig);

    internal static Activity? SetOpenInferenceTag(
        this Activity? activity,
        string key,
        double value,
        OpenInferenceTraceConfig traceConfig) =>
        SetAttribute(activity, key, value, traceConfig);

    internal static Activity? RecordOpenInferenceException(
        this Activity? activity,
        Exception exception,
        bool escaped = false)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (activity is null)
        {
            return null;
        }

        activity.SetStatus(ActivityStatusCode.Error, exception.Message);
        if (activity.IsAllDataRequested)
        {
            activity.AddException(exception, new TagList
            {
                { OpenInferenceAttributes.ExceptionEscaped, escaped }
            });
        }

        return activity;
    }

    private static Activity? SetAttribute(
        Activity? activity,
        string key,
        object value,
        OpenInferenceTraceConfig traceConfig)
    {
        if (activity is null || !activity.IsAllDataRequested)
        {
            return activity;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(traceConfig);

        var action = traceConfig.GetPrivacyAction(key);
        activity.SetTag(key, action switch
        {
            PrivacyAction.Omit => null,
            PrivacyAction.Redact => OpenInferenceTraceConfig.RedactedValue,
            _ => value
        });
        return activity;
    }

    private static void AddAmbientTags(ActivityTagsCollection tags)
    {
        var context = OpenInferenceContextScope.Current;
        if (context is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(context.SessionId))
        {
            tags[OpenInferenceAttributes.SessionId] = context.SessionId;
        }

        if (!string.IsNullOrWhiteSpace(context.UserId))
        {
            tags[OpenInferenceAttributes.UserId] = context.UserId;
        }

        if (context.Tags is not null)
        {
            tags[OpenInferenceAttributes.TagTags] = context.Tags.ToArray();
        }
    }

    private static void AddTag(
        ActivityTagsCollection tags,
        string key,
        object? value,
        OpenInferenceTraceConfig traceConfig)
    {
        if (value is null)
        {
            return;
        }

        var action = traceConfig.GetPrivacyAction(key);
        if (action == PrivacyAction.Omit)
        {
            return;
        }

        tags[key] = action == PrivacyAction.Redact
            ? OpenInferenceTraceConfig.RedactedValue
            : value is string[] strings ? strings.ToArray() : value;
    }
}
