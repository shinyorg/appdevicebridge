using System.Text.Json.Serialization;
using Shiny.AppDeviceBridge.Client;

namespace Shiny.AppDeviceBridge.Health.Client;

public enum HealthDataType
{
    StepCount, HeartRate, Calories, Distance, Weight, Height, BodyFatPercentage, RestingHeartRate, BloodPressure,
    OxygenSaturation, SleepDuration, Hydration, MenstruationFlow, BloodGlucose, BodyTemperature, BasalBodyTemperature,
    RespiratoryRate, Vo2Max, HeartRateVariability, LeanBodyMass, BasalEnergyBurned, ActiveEnergyBurned, FloorsClimbed,
    WheelchairPushes, Speed, Power, SexualActivity, OvulationTest, CervicalMucus, IntermenstrualBleeding, Workout, Nutrition
}

/// <summary>How a type is read: bucketed numbers, bucketed blood pressure pairs, or individual records.</summary>
public enum HealthDataKind { Numeric, BloodPressure, Record }

public enum HealthAccessType { Read, Write, ReadWrite }

/// <summary>The bucket size for numeric and blood pressure reads.</summary>
public enum HealthInterval { Minutes, Hours, Days }

public enum MenstrualFlow { Unspecified, None, Light, Medium, Heavy }

public enum SexualActivityProtection { Unspecified, Protected, Unprotected }

public enum OvulationTestOutcome { Inconclusive, Positive, High, Negative }

public enum CervicalMucusAppearance { Unspecified, Dry, Sticky, Creamy, Watery, EggWhite }

public enum MealType { Unknown, Breakfast, Lunch, Dinner, Snack }

public enum WorkoutType
{
    Other, Running, Walking, Hiking, Cycling, Swimming, Rowing, Elliptical, StairClimbing, StrengthTraining,
    HighIntensityIntervalTraining, Yoga, Pilates, Tennis, Basketball, Soccer, Baseball, Golf, Boxing, MartialArts, Dancing
}

/// <param name="Unit">What values are measured in — <c>count</c>, <c>bpm</c>, <c>kg</c>; null for record types.</param>
/// <param name="Bucketed">Whether reads are bucketed by interval.</param>
public sealed record HealthTypeInfo(HealthDataType Type, HealthDataKind Kind, string? Unit, bool Bucketed);

public sealed record HealthListener(HealthDataType Type);

/// <param name="Available">False where the store is missing — Health Connect not installed.</param>
/// <param name="Listeners">The types being watched.</param>
public sealed record HealthStatus(bool Available, IReadOnlyList<HealthTypeInfo> Types, IReadOnlyList<HealthListener> Listeners);

public sealed record HealthPermission(HealthDataType Type, HealthAccessType Access = HealthAccessType.Read);

public sealed record HealthAccessRequest(IReadOnlyList<HealthPermission> Permissions);

/// <param name="Granted">Whether the platform reported success. iOS never says whether read access was granted.</param>
public sealed record HealthAccessResult(HealthDataType Type, bool Granted);

public sealed record HealthAccessResults(IReadOnlyList<HealthAccessResult> Results);

/// <summary>
/// One sample. Which fields are set depends on the type: <see cref="Value"/> for numeric types, <see cref="Systolic"/>
/// and <see cref="Diastolic"/> for blood pressure, and the fields named after each record type for the rest.
/// </summary>
public sealed record HealthSample(
    DateTimeOffset Start,
    DateTimeOffset End,
    double? Value = null,
    double? Systolic = null,
    double? Diastolic = null,
    MenstrualFlow? Flow = null,
    bool? IsCycleStart = null,
    SexualActivityProtection? Protection = null,
    OvulationTestOutcome? Outcome = null,
    CervicalMucusAppearance? Appearance = null,
    WorkoutType? Workout = null,
    double? TotalEnergyKilocalories = null,
    double? TotalDistanceMeters = null,
    string? Title = null,
    MealType? Meal = null,
    string? Name = null,
    double? EnergyKilocalories = null,
    double? ProteinGrams = null,
    double? CarbohydratesGrams = null,
    double? TotalFatGrams = null,
    double? FiberGrams = null,
    double? SugarGrams = null,
    double? SodiumGrams = null,
    double? CholesterolGrams = null
);

/// <param name="Unit">What values are measured in; null for record types.</param>
public sealed record HealthSamples(HealthDataType Type, string? Unit, IReadOnlyList<HealthSample> Samples);

/// <summary>
/// A sample to write. <see cref="End"/> defaults to <see cref="Start"/>. Set the fields the type needs:
/// <see cref="Value"/> for numeric types, <see cref="Systolic"/> and <see cref="Diastolic"/> for blood pressure,
/// <see cref="Flow"/> for menstruation flow, <see cref="Outcome"/> for an ovulation test, <see cref="Workout"/> for a
/// workout. Numbers must be finite and not negative; title and name at most 256 characters.
/// </summary>
public sealed class HealthSampleInput
{
    public required DateTimeOffset Start { get; init; }
    public DateTimeOffset? End { get; init; }
    public double? Value { get; init; }
    public double? Systolic { get; init; }
    public double? Diastolic { get; init; }
    public MenstrualFlow? Flow { get; init; }
    public bool? IsCycleStart { get; init; }
    public SexualActivityProtection? Protection { get; init; }
    public OvulationTestOutcome? Outcome { get; init; }
    public CervicalMucusAppearance? Appearance { get; init; }
    public WorkoutType? Workout { get; init; }
    public double? TotalEnergyKilocalories { get; init; }
    public double? TotalDistanceMeters { get; init; }
    public string? Title { get; init; }
    public MealType? Meal { get; init; }
    public string? Name { get; init; }
    public double? EnergyKilocalories { get; init; }
    public double? ProteinGrams { get; init; }
    public double? CarbohydratesGrams { get; init; }
    public double? TotalFatGrams { get; init; }
    public double? FiberGrams { get; init; }
    public double? SugarGrams { get; init; }
    public double? SodiumGrams { get; init; }
    public double? CholesterolGrams { get; init; }
}

/// <param name="PollingIntervalSeconds">How often Android polls, 5–300 seconds. iOS is pushed and ignores it.</param>
public sealed record HealthListenerRequest(int? PollingIntervalSeconds = null);

public sealed record HealthReading(HealthDataType Type, HealthSample Sample);

/// <param name="Error">Why it ended, when it failed rather than being stopped.</param>
public sealed record HealthListenerStopped(HealthDataType Type, string? Error);

/// <summary>Serialization for every health contract, shared by the page's client and the native bridge.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(HealthStatus))]
[JsonSerializable(typeof(HealthAccessRequest))]
[JsonSerializable(typeof(HealthAccessResults))]
[JsonSerializable(typeof(HealthSamples))]
[JsonSerializable(typeof(HealthSampleInput))]
[JsonSerializable(typeof(HealthListenerRequest))]
[JsonSerializable(typeof(HealthListener))]
[JsonSerializable(typeof(HealthReading))]
[JsonSerializable(typeof(HealthListenerStopped))]
public partial class HealthJsonContext : JsonSerializerContext;
