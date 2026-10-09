namespace Beacon.Domain;

/// <summary>
/// The fixed data set every chapter uses, so listings, screenshots and tests agree.
/// "Today" is anchored to <see cref="Today"/> rather than the clock, so recorded streams stay stable.
/// </summary>
public static class SeedData
{
    public static readonly DateOnly Today = new(2026, 11, 16);

    private static readonly TimeSpan Offset = TimeSpan.FromHours(-5);

    private static DateTimeOffset At(int hour, int minute = 0) =>
        new(Today.Year, Today.Month, Today.Day, hour, minute, 0, Offset);

    public static IReadOnlyList<Customer> Customers { get; } =
    [
        new("CUST-100", "Harbor View Dental", "12 Wharf Street, Portsmouth", "555-0141", ServiceTier.Priority, 43.0718, -70.7626),
        new("CUST-200", "Maple Grove Apartments", "480 Maple Avenue, Dover", "555-0177", ServiceTier.Standard, 43.1979, -70.8737),
        new("CUST-300", "Northgate Data Center", "9 Innovation Drive, Durham", "555-0102", ServiceTier.Premium, 43.1340, -70.9264),
    ];

    public static IReadOnlyList<Equipment> Equipment { get; } =
    [
        new("EQ-501", "CUST-100", EquipmentKind.AirConditioner, "Contoso Air", "CA-36X", "CA36X-2219-0441", new(2019, 5, 14)),
        new("EQ-502", "CUST-100", EquipmentKind.Furnace, "Fabrikam", "FX-90", "FX90-1187-2203", new(2016, 10, 2)),
        new("EQ-601", "CUST-200", EquipmentKind.HeatPump, "Contoso Air", "HP-24S", null, new(2021, 3, 30)),
        new("EQ-602", "CUST-200", EquipmentKind.Boiler, "Litware", "LB-150", "LB150-0090-7731", new(2012, 9, 18)),
        new("EQ-701", "CUST-300", EquipmentKind.RooftopUnit, "Contoso Air", "RT-120", "RT120-5521-0007", new(2023, 7, 11)),
    ];

    public static IReadOnlyList<Technician> Technicians { get; } =
    [
        new("TECH-1", "Priya Raman", "Seacoast", ["refrigeration", "heat pumps", "controls"]),
        new("TECH-2", "Marcus Bell", "Seacoast", ["furnaces", "boilers", "gas"]),
        new("TECH-3", "Elena Ortiz", "Inland", ["rooftop units", "refrigeration", "commercial"]),
        new("TECH-4", "Sam Whitaker", "Inland", ["heat pumps", "air conditioning"]),
    ];

    public static IReadOnlyList<WorkOrder> WorkOrders { get; } =
    [
        new("WO-1001", "CUST-100", "EQ-501", "Air conditioner short-cycling and blowing warm air",
            WorkOrderStatus.Scheduled, WorkOrderPriority.High, "TECH-1", At(9),
            [
                new("C1", "Check thermostat settings and wiring", false),
                new("C2", "Measure refrigerant pressures", false),
                new("C3", "Inspect run capacitor", false),
                new("C4", "Test compressor amperage", false),
            ],
            ["Customer reports the unit stops after a few minutes."]),
        new("WO-1002", "CUST-100", "EQ-502", "Annual furnace inspection",
            WorkOrderStatus.Open, WorkOrderPriority.Normal, null, null,
            [
                new("C1", "Inspect heat exchanger", false),
                new("C2", "Replace air filter", false),
                new("C3", "Test carbon monoxide levels", false),
            ],
            []),
        new("WO-1003", "CUST-200", "EQ-601", "Heat pump making a loud grinding noise",
            WorkOrderStatus.Scheduled, WorkOrderPriority.High, "TECH-4", At(11),
            [
                new("C1", "Record serial number from nameplate", false),
                new("C2", "Inspect outdoor fan motor and bearings", false),
                new("C3", "Check defrost cycle", false),
            ],
            ["Serial number missing from the equipment record."]),
        new("WO-1004", "CUST-200", "EQ-602", "Boiler pressure dropping overnight",
            WorkOrderStatus.AwaitingParts, WorkOrderPriority.Normal, "TECH-2", At(14),
            [
                new("C1", "Inspect pressure relief valve", true),
                new("C2", "Check expansion tank charge", false),
            ],
            ["Expansion tank suspected; part on order."]),
        new("WO-1005", "CUST-300", "EQ-701", "Rooftop unit compressor fault alarm",
            WorkOrderStatus.InProgress, WorkOrderPriority.Emergency, "TECH-3", At(8),
            [
                new("C1", "Read controller fault codes", true),
                new("C2", "Test compressor windings", false),
                new("C3", "Check contactor and wiring", false),
            ],
            ["Server room temperature rising; customer on Premium tier."]),
        new("WO-1006", "CUST-300", "EQ-701", "Quarterly filter change",
            WorkOrderStatus.Completed, WorkOrderPriority.Low, "TECH-3", At(7),
            [new("C1", "Replace filters", true)],
            []),
    ];

    public static IReadOnlyList<Part> Parts { get; } =
    [
        new("CAP-45-5", "Dual run capacitor 45/5 uF", 38.50m, 24, "Portsmouth", ["CA-36X", "HP-24S"]),
        new("CMP-CA36", "Scroll compressor 3-ton", 1795.00m, 2, "Portsmouth", ["CA-36X"]),
        new("CMP-RT120", "Tandem scroll compressor 10-ton", 4820.00m, 1, "Durham", ["RT-120"]),
        new("FAN-HP24", "Outdoor fan motor 1/4 hp", 212.00m, 5, "Dover", ["HP-24S"]),
        new("BRG-KIT-1", "Fan bearing kit", 46.75m, 12, "Dover", ["HP-24S", "CA-36X"]),
        new("CNT-40A", "Contactor 40 A two-pole", 29.90m, 30, "Portsmouth", ["CA-36X", "HP-24S", "RT-120"]),
        new("EXP-TANK-4", "Expansion tank 4.4 gal", 118.00m, 0, "Dover", ["LB-150"]),
        new("PRV-30", "Pressure relief valve 30 psi", 54.20m, 9, "Dover", ["LB-150"]),
        new("FLT-20X25", "Pleated air filter 20x25", 12.40m, 120, "Portsmouth", ["FX-90", "CA-36X", "HP-24S"]),
        new("IGN-FX90", "Hot surface igniter", 64.00m, 7, "Portsmouth", ["FX-90"]),
    ];

    public static IReadOnlyList<WarrantyStatus> Warranties { get; } =
    [
        new("EQ-501", true, new(2029, 5, 14), "Parts: 10 years; labor: 2 years (labor expired)"),
        new("EQ-502", false, new(2026, 10, 2), "Expired"),
        new("EQ-601", true, new(2031, 3, 30), "Parts and labor: 10 years"),
        new("EQ-602", false, null, "No warranty on file"),
        new("EQ-701", true, new(2028, 7, 11), "Compressor: 5 years; other parts: 1 year"),
    ];

    public static IReadOnlyList<FaultRecord> FaultHistory { get; } =
    [
        new("EQ-501", new(2025, 7, 2), "E3", "High head pressure"),
        new("EQ-501", new(2026, 6, 19), "E3", "High head pressure"),
        new("EQ-501", new(2026, 11, 14), "E5", "Compressor overload trip"),
        new("EQ-701", new(2026, 3, 8), "C12", "Low suction pressure"),
        new("EQ-701", new(2026, 9, 27), "C31", "Compressor current imbalance"),
        new("EQ-701", new(2026, 11, 16), "C33", "Compressor locked rotor"),
        new("EQ-601", new(2026, 1, 21), "D07", "Defrost sensor out of range"),
    ];

    public static IReadOnlyList<ScheduleSlot> Schedule { get; } =
    [
        new("TECH-1", At(9), At(11), "WO-1001"),
        new("TECH-1", At(13), At(17), null),
        new("TECH-2", At(8), At(12), null),
        new("TECH-2", At(14), At(16), "WO-1004"),
        new("TECH-3", At(7), At(8), "WO-1006"),
        new("TECH-3", At(8), At(12), "WO-1005"),
        new("TECH-4", At(11), At(13), "WO-1003"),
        new("TECH-4", At(14), At(17), null),
    ];
}
