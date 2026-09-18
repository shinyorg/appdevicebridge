using System.Collections.Concurrent;
using System.Globalization;
using System.Security;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Client;
using Shiny.Health;
using Shiny.Net.HttpServer;
using Contracts = Shiny.AppDeviceBridge.Health.Client;
using static Shiny.AppDeviceBridge.Health.HealthContractMapping;

namespace Shiny.AppDeviceBridge.Health;

public static class HealthBridgeExtensions
{
    /// <summary>
    /// Adds <c>/_bridge/health</c> and registers Shiny's health service on iOS (HealthKit) and Android (Health
    /// Connect) — there is nothing else to call. Other platforms have no health store: the bridge answers 501 there.
    /// <code>
    /// bridge.AddHealthBridge();
    /// </code>
    /// <para>
    /// The platform setup is Shiny.Health's: the HealthKit entitlement and the <c>NSHealthShareUsageDescription</c>
    /// and <c>NSHealthUpdateUsageDescription</c> keys on iOS; a <c>android.permission.health.*</c> permission per
    /// type read or written, the Health Connect <c>&lt;queries&gt;</c> entry and the permission rationale
    /// activity on Android.
    /// </para>
    /// </summary>
    public static TBuilder AddHealthBridge<TBuilder>(this TBuilder bridge)
        where TBuilder : AppDeviceBridgeBuilder
    {
        ArgumentNullException.ThrowIfNull(bridge);

#if ANDROID || IOS
        if (!bridge.Services.Any(x => x.ServiceType == typeof(IHealthService)))
            bridge.Services.AddHealthIntegration();
#endif

        bridge.AddBridge<HealthBridge>();
        return bridge;
    }
}

/// <summary>
/// <c>/_bridge/health</c> over <see cref="IHealthService"/>.
/// <code>
/// GET    /_bridge/health                     { "available": true, "types": [...], "listeners": [...] }
/// POST   /_bridge/health/access              { "permissions": [{ "type": "StepCount", "access": "Read" }] }
/// GET    /_bridge/health/samples/{type}?start=…&amp;end=…&amp;interval=minutes|hours|days
/// POST   /_bridge/health/samples/{type}      { "start": "…", "end": "…", "value": 75 }   (fields depend on the type)
/// POST   /_bridge/health/listeners/{type}    { "pollingIntervalSeconds": 5 }   (Android polls; iOS is pushed)
/// DELETE /_bridge/health/listeners/{type}
///
/// events: health.reading, health.stopped
/// </code>
/// <para>
/// Numeric types and blood pressure are bucketed by <c>interval</c> (days when omitted). Cycle-tracking types,
/// workouts and nutrition are individual records, and ignore it. A range may span at most 366 days and 2000
/// buckets. Health values are never logged, and only reach the event stream while the page has a listener
/// running for that type.
/// </para>
/// <para>
/// Listeners are for whoever listens for <c>health.reading</c>: starting one needs such a listener, and every one
/// stops once the last of them is gone.
/// </para>
/// </summary>
public sealed class HealthBridge : IWebAppBridge, IDisposable
{
    const int MaxListeners = 8;
    const int MaxBuckets = 2000;
    const int MaxPermissions = 64;
    const int MaxTextLength = 256;
    static readonly TimeSpan MaxRange = TimeSpan.FromDays(366);

    readonly IHealthService? health;
    readonly IWebAppMainThread mainThread;
    readonly WebAppEventSource<Contracts.HealthReading> readings = new();
    readonly WebAppEventSource<Contracts.HealthListenerStopped> stopped = new();
    readonly ConcurrentDictionary<DataType, CancellationTokenSource> listeners = new();

    public HealthBridge(IServiceProvider services)
    {
        this.health = services.GetOptionalService<IHealthService>();
        this.mainThread = services.GetRequiredService<IWebAppMainThread>();
    }

    public string Name => "health";

    public bool IsSupported => this.health is not null;

    public void Map(WebAppBridgeRoutes routes) => routes
        .MapEvent("health.reading", ct => this.readings.ListenAsync(this.OnReadingListenerStopped, ct), Contracts.HealthJsonContext.Default.HealthReading)
        .MapEvent("health.stopped", this.stopped.ListenAsync, Contracts.HealthJsonContext.Default.HealthListenerStopped)
        .MapGet("", this.StatusAsync)
        .MapPost("/access", this.RequestAccessAsync)
        .MapGet("/samples/{type}", this.ReadAsync)
        .MapPost("/samples/{type}", this.WriteAsync)
        .MapPost("/listeners/{type}", this.StartListenerAsync)
        .MapDelete("/listeners/{type}", this.StopListenerAsync);

    ValueTask StatusAsync(HttpContext context)
    {
        if (this.health is not { } h)
            return WebAppBridgeResults.NotSupported(context, "Health");

        var types = Enum.GetValues<DataType>()
            .Select(x => new Contracts.HealthTypeInfo(Convert(x), KindOf(x), UnitOf(x), KindOf(x) != Contracts.HealthDataKind.Record))
            .ToList();

        return WebAppBridgeResults.Json(
            context,
            new Contracts.HealthStatus(IsAvailable(h), types, [.. this.listeners.Keys.Select(x => new Contracts.HealthListener(Convert(x)))]),
            Contracts.HealthJsonContext.Default.HealthStatus
        );
    }

    ValueTask RequestAccessAsync(HttpContext context) => this.WithHealth(context, async h =>
    {
        var body = await WebAppBridgeResults.ReadBodyAsync(context, Contracts.HealthJsonContext.Default.HealthAccessRequest);
        if (body?.Permissions is not { Count: > 0 and <= MaxPermissions } permissions
            || permissions.Any(x => x is null || !Enum.IsDefined(x.Type) || !Enum.IsDefined(x.Access)))
        {
            await WebAppBridgeResults.BadRequest(context, $"Expected {{ \"permissions\": [{{ \"type\": \"StepCount\", \"access\": \"Read\" | \"Write\" | \"ReadWrite\" }}] }} with 1 to {MaxPermissions} entries.");
            return;
        }

        var requested = permissions.Select(x => (Convert(x.Access), Convert(x.Type))).Distinct().ToArray();

        // The HealthKit sheet and the Health Connect activity are UI.
        var results = await this.mainThread.InvokeAsync(() => h.RequestPermissions(requested));

        await WebAppBridgeResults.Json(
            context,
            new Contracts.HealthAccessResults([.. results.Select(x => new Contracts.HealthAccessResult(Convert(x.Type), x.Success))]),
            Contracts.HealthJsonContext.Default.HealthAccessResults
        );
    });

    ValueTask ReadAsync(HttpContext context) => this.WithHealth(context, async h =>
    {
        if (!TryGetType(context, out var type))
        {
            await BadType(context);
            return;
        }

        var query = context.Request.Query;
        if (!TryParseDate(query["start"].ToString(), out var start) || !TryParseDate(query["end"].ToString(), out var end) || end <= start)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected ISO-8601 start and end query values, with end after start.");
            return;
        }

        if (end - start > MaxRange)
        {
            await WebAppBridgeResults.BadRequest(context, $"A range may span at most {MaxRange.TotalDays:0} days.");
            return;
        }

        if (!TryParseInterval(query["interval"].ToString(), out var interval, out var bucket))
        {
            await WebAppBridgeResults.BadRequest(context, "Expected interval to be minutes, hours or days.");
            return;
        }

        if (KindOf(type) != Contracts.HealthDataKind.Record && (end - start).Ticks / bucket.Ticks > MaxBuckets)
        {
            await WebAppBridgeResults.BadRequest(context, $"That range and interval make more than {MaxBuckets} buckets. Use a coarser interval or a shorter range.");
            return;
        }

        var results = await Query(h, type, start, end, interval, context.RequestAborted);

        await WebAppBridgeResults.Json(
            context,
            new Contracts.HealthSamples(Convert(type), UnitOf(type), [.. results.Select(ToSample)]),
            Contracts.HealthJsonContext.Default.HealthSamples
        );
    });

    ValueTask WriteAsync(HttpContext context) => this.WithHealth(context, async h =>
    {
        if (!TryGetType(context, out var type))
        {
            await BadType(context);
            return;
        }

        var body = await WebAppBridgeResults.ReadBodyAsync(context, Contracts.HealthJsonContext.Default.HealthSampleInput);
        if (body is not { Start: var start } || start == default)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"start\": \"…\", \"end\": \"…\", … } with the fields for the type.");
            return;
        }

        var end = body.End ?? start;
        if (end < start || end - start > MaxRange)
        {
            await WebAppBridgeResults.BadRequest(context, $"end must not be before start, and a sample may span at most {MaxRange.TotalDays:0} days.");
            return;
        }

        if (Validate(body) is { } invalid)
        {
            await WebAppBridgeResults.BadRequest(context, invalid);
            return;
        }

        if (Write(h, type, start, end, body, context.RequestAborted) is not { } write)
        {
            await WebAppBridgeResults.BadRequest(context, $"{type} needs {RequiredFields(type)}.");
            return;
        }

        await write;
        await WebAppBridgeResults.NoContent(context);
    });

    ValueTask StartListenerAsync(HttpContext context) => this.WithHealth(context, async h =>
    {
        if (!TryGetType(context, out var type))
        {
            await BadType(context);
            return;
        }

        if (!this.readings.HasListeners)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status409Conflict, "not_listening", "Listen for health.reading first: readings arrive as events.");
            return;
        }

        if (this.listeners.ContainsKey(type))
        {
            await WebAppBridgeResults.Json(context, new Contracts.HealthListener(Convert(type)), Contracts.HealthJsonContext.Default.HealthListener);
            return;
        }

        if (this.listeners.Count >= MaxListeners)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status429TooManyRequests, "too_many_listeners", $"At most {MaxListeners} types can be observed at once. Stop one first.");
            return;
        }

        // The body is optional; a malformed one is still refused rather than silently ignored.
        Contracts.HealthListenerRequest? body = null;
        if (context.Request.ContentLength is > 0)
        {
            body = await WebAppBridgeResults.ReadBodyAsync(context, Contracts.HealthJsonContext.Default.HealthListenerRequest);
            if (body is null)
            {
                await WebAppBridgeResults.BadRequest(context, "Expected { \"pollingIntervalSeconds\": 5 } or no body.");
                return;
            }
        }

        var polling = body?.PollingIntervalSeconds is { } seconds ? TimeSpan.FromSeconds(Math.Clamp(seconds, 5, 300)) : (TimeSpan?)null;
        var cancellation = new CancellationTokenSource();

        if (!this.listeners.TryAdd(type, cancellation))
        {
            cancellation.Dispose();
            await WebAppBridgeResults.Json(context, new Contracts.HealthListener(Convert(type)), Contracts.HealthJsonContext.Default.HealthListener);
            return;
        }

        _ = Task.Run(() => this.ObserveAsync(h, type, polling, cancellation));

        // The last reading listener may have gone since the check above, and then nothing would ever stop this one.
        if (!this.readings.HasListeners)
            TryCancel(cancellation);

        await WebAppBridgeResults.Json(context, new Contracts.HealthListener(Convert(type)), Contracts.HealthJsonContext.Default.HealthListener);
    });

    async ValueTask StopListenerAsync(HttpContext context)
    {
        if (this.health is null)
        {
            await WebAppBridgeResults.NotSupported(context, "Health");
            return;
        }

        if (!TryGetType(context, out var type))
        {
            await BadType(context);
            return;
        }

        if (!this.listeners.TryGetValue(type, out var cancellation))
        {
            await WebAppBridgeResults.NotFound(context, $"No listener is running for {type}.");
            return;
        }

        cancellation.Cancel();
        await WebAppBridgeResults.NoContent(context);
    }

    async Task ObserveAsync(IHealthService h, DataType type, TimeSpan? polling, CancellationTokenSource cancellation)
    {
        string? error = null;

        try
        {
            await foreach (var result in h.Observe(type, polling, cancellation.Token).ConfigureAwait(false))
                this.readings.Publish(new Contracts.HealthReading(Convert(type), ToSample(result)));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        finally
        {
            this.listeners.TryRemove(new KeyValuePair<DataType, CancellationTokenSource>(type, cancellation));
            cancellation.Dispose();
            this.stopped.Publish(new Contracts.HealthListenerStopped(Convert(type), error));
        }
    }

    void OnReadingListenerStopped(int remaining)
    {
        // Nobody is left to receive readings, and a page that comes back starts its own listeners again.
        if (remaining > 0)
            return;

        foreach (var cancellation in this.listeners.Values)
            TryCancel(cancellation);
    }

    async ValueTask WithHealth(HttpContext context, Func<IHealthService, ValueTask> action)
    {
        if (this.health is not { } h)
        {
            await WebAppBridgeResults.NotSupported(context, "Health");
            return;
        }

        if (!IsAvailable(h))
        {
            await WebAppBridgeResults.Error(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "health_unavailable",
                "The health store is not available on this device. On Android, install or update Health Connect."
            );
            return;
        }

        try
        {
            await action(h);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (!context.Response.HasStarted && IsAccessDenied(ex))
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status403Forbidden, "access_denied", "Access to that health data has not been granted. POST /_bridge/health/access first.");
        }
        catch (Exception ex) when (!context.Response.HasStarted && ex is NotSupportedException or NotImplementedException)
        {
            await WebAppBridgeResults.Error(context, StatusCodes.Status501NotImplemented, "not_supported", ex.Message);
        }
        catch (ArgumentException ex) when (!context.Response.HasStarted)
        {
            await WebAppBridgeResults.BadRequest(context, ex.Message);
        }
        catch (Exception ex) when (!context.Response.HasStarted)
        {
            // The exception's message, never the data: health values do not belong in errors or logs.
            await WebAppBridgeResults.Error(context, StatusCodes.Status500InternalServerError, "health_failed", ex.Message);
        }
    }

    // ---- reads

    static async Task<IReadOnlyList<HealthResult>> Query(IHealthService h, DataType type, DateTimeOffset start, DateTimeOffset end, Interval interval, CancellationToken ct) => type switch
    {
        DataType.StepCount => await List(h.GetStepCounts(start, end, interval, ct)),
        DataType.HeartRate => await List(h.GetAverageHeartRate(start, end, interval, ct)),
        DataType.Calories => await List(h.GetCalories(start, end, interval, ct)),
        DataType.Distance => await List(h.GetDistances(start, end, interval, ct)),
        DataType.Weight => await List(h.GetWeight(start, end, interval, ct)),
        DataType.Height => await List(h.GetHeight(start, end, interval, ct)),
        DataType.BodyFatPercentage => await List(h.GetBodyFatPercentage(start, end, interval, ct)),
        DataType.RestingHeartRate => await List(h.GetRestingHeartRate(start, end, interval, ct)),
        DataType.BloodPressure => await List(h.GetBloodPressure(start, end, interval, ct)),
        DataType.OxygenSaturation => await List(h.GetOxygenSaturation(start, end, interval, ct)),
        DataType.SleepDuration => await List(h.GetSleepDuration(start, end, interval, ct)),
        DataType.Hydration => await List(h.GetHydration(start, end, interval, ct)),
        DataType.BloodGlucose => await List(h.GetBloodGlucose(start, end, interval, ct)),
        DataType.BodyTemperature => await List(h.GetBodyTemperature(start, end, interval, ct)),
        DataType.BasalBodyTemperature => await List(h.GetBasalBodyTemperature(start, end, interval, ct)),
        DataType.RespiratoryRate => await List(h.GetRespiratoryRate(start, end, interval, ct)),
        DataType.Vo2Max => await List(h.GetVo2Max(start, end, interval, ct)),
        DataType.HeartRateVariability => await List(h.GetHeartRateVariability(start, end, interval, ct)),
        DataType.LeanBodyMass => await List(h.GetLeanBodyMass(start, end, interval, ct)),
        DataType.BasalEnergyBurned => await List(h.GetBasalEnergyBurned(start, end, interval, ct)),
        DataType.ActiveEnergyBurned => await List(h.GetActiveEnergyBurned(start, end, interval, ct)),
        DataType.FloorsClimbed => await List(h.GetFloorsClimbed(start, end, interval, ct)),
        DataType.WheelchairPushes => await List(h.GetWheelchairPushes(start, end, interval, ct)),
        DataType.Speed => await List(h.GetSpeed(start, end, interval, ct)),
        DataType.Power => await List(h.GetPower(start, end, interval, ct)),
        DataType.MenstruationFlow => await List(h.GetMenstruationFlow(start, end, ct)),
        DataType.SexualActivity => await List(h.GetSexualActivity(start, end, ct)),
        DataType.OvulationTest => await List(h.GetOvulationTests(start, end, ct)),
        DataType.CervicalMucus => await List(h.GetCervicalMucus(start, end, ct)),
        DataType.IntermenstrualBleeding => await List(h.GetIntermenstrualBleeding(start, end, ct)),
        DataType.Workout => await List(h.GetWorkouts(start, end, ct)),
        DataType.Nutrition => await List(h.GetNutrition(start, end, ct)),
        _ => throw new NotSupportedException($"{type} cannot be read.")
    };

    static async Task<IReadOnlyList<HealthResult>> List<T>(Task<IList<T>> task) where T : HealthResult => [.. await task];

    static Contracts.HealthSample ToSample(HealthResult result) => result switch
    {
        NumericHealthResult x => new(x.Start, x.End, Value: x.Value),
        BloodPressureResult x => new(x.Start, x.End, Systolic: x.Systolic, Diastolic: x.Diastolic),
        MenstruationFlowResult x => new(x.Start, x.End, Flow: Convert<MenstrualFlow, Contracts.MenstrualFlow>(x.Flow), IsCycleStart: x.IsCycleStart),
        SexualActivityResult x => new(x.Start, x.End, Protection: Convert<SexualActivityProtection, Contracts.SexualActivityProtection>(x.Protection)),
        OvulationTestResult x => new(x.Start, x.End, Outcome: Convert<OvulationTestOutcome, Contracts.OvulationTestOutcome>(x.Outcome)),
        CervicalMucusResult x => new(x.Start, x.End, Appearance: Convert<CervicalMucusAppearance, Contracts.CervicalMucusAppearance>(x.Appearance)),
        WorkoutResult x => new(
            x.Start,
            x.End,
            Workout: Convert<WorkoutType, Contracts.WorkoutType>(x.Workout),
            TotalEnergyKilocalories: x.TotalEnergyKilocalories,
            TotalDistanceMeters: x.TotalDistanceMeters,
            Title: x.Title
        ),
        NutritionResult x => new(
            x.Start,
            x.End,
            Meal: Convert<MealType, Contracts.MealType>(x.Meal),
            Name: x.Name,
            EnergyKilocalories: x.EnergyKilocalories,
            ProteinGrams: x.ProteinGrams,
            CarbohydratesGrams: x.CarbohydratesGrams,
            TotalFatGrams: x.TotalFatGrams,
            FiberGrams: x.FiberGrams,
            SugarGrams: x.SugarGrams,
            SodiumGrams: x.SodiumGrams,
            CholesterolGrams: x.CholesterolGrams
        ),
        _ => new(result.Start, result.End)
    };

    // ---- writes

    /// <summary>The write for the type, or null when the body lacks a field the type needs.</summary>
    static Task? Write(IHealthService h, DataType type, DateTimeOffset start, DateTimeOffset end, Contracts.HealthSampleInput body, CancellationToken ct) => type switch
    {
        DataType.BloodPressure => body is { Systolic: { } systolic, Diastolic: { } diastolic }
            ? h.Write(new BloodPressureResult(start, end, systolic, diastolic), ct)
            : null,
        DataType.MenstruationFlow => body.Flow is { } flow
            ? h.Write(new MenstruationFlowResult(start, end, Convert<Contracts.MenstrualFlow, MenstrualFlow>(flow), body.IsCycleStart ?? false), ct)
            : null,
        DataType.SexualActivity => h.Write(new SexualActivityResult(start, end, Convert<Contracts.SexualActivityProtection, SexualActivityProtection>(body.Protection ?? Contracts.SexualActivityProtection.Unspecified)), ct),
        DataType.OvulationTest => body.Outcome is { } outcome
            ? h.Write(new OvulationTestResult(start, end, Convert<Contracts.OvulationTestOutcome, OvulationTestOutcome>(outcome)), ct)
            : null,
        DataType.CervicalMucus => h.Write(new CervicalMucusResult(start, end, Convert<Contracts.CervicalMucusAppearance, CervicalMucusAppearance>(body.Appearance ?? Contracts.CervicalMucusAppearance.Unspecified)), ct),
        DataType.IntermenstrualBleeding => h.Write(new IntermenstrualBleedingResult(start, end), ct),
        DataType.Workout => body.Workout is { } workout
            ? h.Write(new WorkoutResult(start, end, Convert<Contracts.WorkoutType, WorkoutType>(workout), body.TotalEnergyKilocalories, body.TotalDistanceMeters, body.Title), ct)
            : null,
        DataType.Nutrition => h.Write(
            new NutritionResult(
                start, end, Convert<Contracts.MealType, MealType>(body.Meal ?? Contracts.MealType.Unknown), body.Name, body.EnergyKilocalories, body.ProteinGrams, body.CarbohydratesGrams,
                body.TotalFatGrams, body.FiberGrams, body.SugarGrams, body.SodiumGrams, body.CholesterolGrams
            ),
            ct
        ),

        // A sleep session's length is its start and end; Shiny ignores the value.
        DataType.SleepDuration => h.Write(new NumericHealthResult(type, start, end, body.Value ?? 0), ct),
        _ => body.Value is { } value ? h.Write(new NumericHealthResult(type, start, end, value), ct) : null
    };

    static string RequiredFields(DataType type) => type switch
    {
        DataType.BloodPressure => "systolic and diastolic",
        DataType.MenstruationFlow => "flow",
        DataType.OvulationTest => "outcome",
        DataType.Workout => "workout",
        _ => "value"
    };

    /// <summary>Checks what JSON binding does not: finite, non-negative numbers, defined enum values, bounded text.</summary>
    static string? Validate(Contracts.HealthSampleInput body)
    {
        double?[] numbers =
        [
            body.Value, body.Systolic, body.Diastolic, body.TotalEnergyKilocalories, body.TotalDistanceMeters, body.EnergyKilocalories,
            body.ProteinGrams, body.CarbohydratesGrams, body.TotalFatGrams, body.FiberGrams, body.SugarGrams, body.SodiumGrams, body.CholesterolGrams
        ];

        if (numbers.Any(x => x is { } n && (!Double.IsFinite(n) || n < 0)))
            return "Numbers must be finite and not negative.";

        if (!Defined(body.Flow) || !Defined(body.Protection) || !Defined(body.Outcome) || !Defined(body.Appearance) || !Defined(body.Workout) || !Defined(body.Meal))
            return "An enum field has a value that is not defined.";

        if (body.Title?.Length > MaxTextLength || body.Name?.Length > MaxTextLength)
            return $"title and name may be at most {MaxTextLength} characters.";

        return null;
    }

    static bool Defined<TEnum>(TEnum? value) where TEnum : struct, Enum => value is not { } v || Enum.IsDefined(v);

    // ---- types

    static Contracts.HealthDataKind KindOf(DataType type) => type switch
    {
        DataType.BloodPressure => Contracts.HealthDataKind.BloodPressure,
        DataType.MenstruationFlow or DataType.SexualActivity or DataType.OvulationTest or DataType.CervicalMucus
            or DataType.IntermenstrualBleeding or DataType.Workout or DataType.Nutrition => Contracts.HealthDataKind.Record,
        _ => Contracts.HealthDataKind.Numeric
    };

    static string? UnitOf(DataType type) => type switch
    {
        DataType.StepCount or DataType.FloorsClimbed or DataType.WheelchairPushes => "count",
        DataType.HeartRate or DataType.RestingHeartRate => "bpm",
        DataType.Calories or DataType.BasalEnergyBurned or DataType.ActiveEnergyBurned => "kcal",
        DataType.Distance or DataType.Height => "m",
        DataType.Weight or DataType.LeanBodyMass => "kg",
        DataType.BodyFatPercentage or DataType.OxygenSaturation => "%",
        DataType.BloodPressure => "mmHg",
        DataType.SleepDuration => "h",
        DataType.Hydration => "L",
        DataType.BloodGlucose => "mg/dL",
        DataType.BodyTemperature or DataType.BasalBodyTemperature => "degC",
        DataType.RespiratoryRate => "breaths/min",
        DataType.Vo2Max => "mL/kg/min",
        DataType.HeartRateVariability => "ms",
        DataType.Speed => "m/s",
        DataType.Power => "W",
        _ => null
    };

    // ---- helpers

    static bool TryGetType(HttpContext context, out DataType type)
    {
        var value = context.Request.RouteValues["type"];

        // Enum.TryParse also accepts numbers, which are not type names.
        type = default;
        return !String.IsNullOrEmpty(value)
            && Char.IsLetter(value[0])
            && Enum.TryParse(value, ignoreCase: true, out type)
            && Enum.IsDefined(type);
    }

    static ValueTask BadType(HttpContext context)
        => WebAppBridgeResults.NotFound(context, "Unknown health data type. GET /_bridge/health lists them.");

    static bool TryParseDate(string value, out DateTimeOffset date)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out date);

    static bool TryParseInterval(string value, out Interval interval, out TimeSpan bucket)
    {
        (interval, bucket) = value.ToLowerInvariant() switch
        {
            "minutes" => (Interval.Minutes, TimeSpan.FromMinutes(1)),
            "hours" => (Interval.Hours, TimeSpan.FromHours(1)),
            "" or "days" => (Interval.Days, TimeSpan.FromDays(1)),
            _ => ((Interval)(-1), TimeSpan.Zero)
        };

        return bucket > TimeSpan.Zero;
    }

    /// <summary>Health Connect's availability check calls into the SDK, which can throw on a device without it.</summary>
    static bool IsAvailable(IHealthService h)
    {
        try
        {
            return h.IsAvailable;
        }
        catch (Exception)
        {
            return false;
        }
    }

    static bool IsAccessDenied(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is UnauthorizedAccessException or SecurityException)
                return true;

#if ANDROID
            // Health Connect refuses an ungranted type by throwing, wrapped in its own exception type.
            if (current is Java.Lang.SecurityException)
                return true;
#endif
        }

        return false;
    }

    static void TryCancel(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The listener ended on its own in the meantime.
        }
    }

    public void Dispose()
    {
        foreach (var cancellation in this.listeners.Values)
            TryCancel(cancellation);
    }
}

static class HealthContractMapping
{
    public static Contracts.HealthDataType Convert(DataType type) => BridgeEnum.Convert<DataType, Contracts.HealthDataType>(type);

    public static DataType Convert(Contracts.HealthDataType type) => BridgeEnum.Convert<Contracts.HealthDataType, DataType>(type);

    public static PermissionType Convert(Contracts.HealthAccessType access) => BridgeEnum.Convert<Contracts.HealthAccessType, PermissionType>(access);

    public static TTo Convert<TFrom, TTo>(TFrom value) where TFrom : struct, Enum where TTo : struct, Enum
        => BridgeEnum.Convert<TFrom, TTo>(value);
}
