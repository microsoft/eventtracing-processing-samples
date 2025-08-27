# EventTracing Migration Guide  
**v1 (1.13.x) → v2 (2.0-preview)** for NuGet Consumers

---

## 1. Overview

This guide explains how to migrate applications consuming the EventTracing NuGet packages from **v1** to **v2**, focusing on changes that affect `TraceProcessor`.

**Good news:**  
- Most `Use*` extension code still works.  
- Core workflow remains unchanged.  
- Migration is mostly package, framework, and type updates.

**Estimated effort:** Low–Medium.

---

## 2. Quick Reference

### ✅ What Stays the Same
- `Use*` extensions (`UseContextSwitchData()`, `UseProcesses()`, etc.)
- Core workflow: enable data sources → `Process()` → access results
- `IPendingResult<T>` pattern for post-processing access
- `TraceProcessor.Create()` (though `TraceProcessorBuilder` is recommended)

### ⚠️ What Changed
| Area | v1 | v2 |
|------|----|----|
| **NuGet packages** | `Microsoft.Windows.EventTracing.Processing.All` | `Microsoft.Windows.EventTracing` |
| **Target framework** | .NET Standard 2.0 | .NET Standard 2.1+ |
| **Process/thread IDs** | `int` / `-1` | `uint` / `uint.MaxValue` |
| **Timestamp types** | `TraceTimestamp`, `TraceDuration`, `TraceTimeRange`, `ITraceTimestampContext` | `Timestamp`, `Duration`, `TimeRange` |
| **Removed APIs** | `Properties` dictionary, `Use()`, `UseCompletion()` | Use `Use*` extensions |
| **Streaming API** | `TraceEventCallback` | `UnparsedEventCallback` |
| **Interfaces** | Multiple CPU/thread/stack event interfaces | Single consolidated interfaces |
| **Timestamp context** | `ITraceTimestampContext` | *Removed* (no replacement) |

---

## 3. Migration Steps

### Step 1 — Update NuGet Packages
```xml
<!-- Remove v1 -->
<PackageReference Include="Microsoft.Windows.EventTracing.Processing.All" Version="1.13.*" />

<!-- Add v2 -->
<PackageReference Include="Microsoft.Windows.EventTracing" Version="2.0.*" />
```

### Step 2 — Update Target Framework
```xml
<!-- Before -->
<TargetFramework>netstandard2.0</TargetFramework>

<!-- After -->
<TargetFramework>netstandard2.1</TargetFramework>
```

### Step 3 — Update Data Types
**Process/Thread IDs**
```csharp
// v1
int processId = someEvent.ProcessId ?? -1;
if (processId == -1) { /* invalid */ }

// v2
uint processId = someEvent.ProcessId ?? uint.MaxValue;
if (processId == uint.MaxValue) { /* invalid */ }
```

#### Timestamp Context Removal
**Breaking Change**: `ITraceTimestampContext` has been removed as part of timestamp simplification in v2.

This widely-used interface from v1 provided timestamp conversion capabilities but was removed to reduce complexity in the timestamp system. All timestamps are now relative by default in v2. Use `ITraceMetadata.GetWallClock()` for timestamp conversion instead:

```csharp
// v1 - No longer available
ITraceTimestampContext timestampContext = ...;
DateTimeOffset wallClock = timestampContext.ToWallClockTime(timestamp);

// v2 - Use ITraceMetadata instead
ITraceMetadata metadata = traceProcessor.UseMetadata();
DateTimeOffset wallClock = metadata.GetWallClock(timestamp);
```

### Step 4 — Update TraceProcessor Creation (Optional)
**v1 (still works in v2)**
```csharp
using var traceProcessor = TraceProcessor.Create(tracePath);
```
**v2 (recommended)**
```csharp
using var traceProcessor = new TraceProcessorBuilder()
    .Build(tracePath);
```
**With settings**
```csharp
var logger = ConsoleLogger.Create(LoggingFilter.WarningPlus, logExceptionStacks: true);

var builder = new TraceProcessorBuilder()
    .WithSettings(new TraceProcessorSettings() { AllowTimeInversion = true })
    .WithLogger(logger);

using var traceProcessor = builder.Build(tracePath);
```

**Note:** If you're using `ITraceProcessorSettings` (which has been updated in v2), you must use `TraceProcessorBuilder` instead of `TraceProcessor.Create()` to pass settings.

### Step 5 — Replace Removed APIs
| v1 API | v2 Replacement |
|--------|----------------|
| `Properties` dictionary | Use relevant `Use*` extensions |
| `Use()` / `UseCompletion()` | Use relevant `Use*` extensions |
| `ITraceTimestampContext` | *No replacement* — widely used interface removed |
| `TraceTimestampValue` | `Timestamp` |
| `TraceTimestamp` | `Timestamp` |
| `TraceDuration` | `Duration` |
| `TraceTimeRange` | `TimeRange` |
| Custom `IEventConsumer` | Use streaming + `UseUnparsedEvents()` |

Example — custom event streaming in v2:
```csharp
var myProviderGuid = new Guid("12345678-1234-1234-1234-123456789012");

traceProcessor.UseStreaming().UseUnparsedEvents(
    new[] { myProviderGuid },
    eventContext =>
    {
        var classicEvent = eventContext.Event.AsClassicEvent;
        if (classicEvent?.Id == 42)
        {
            var data = classicEvent.Data;
            var value = EventDataReader.ReadUInt32(ref data);
            Console.WriteLine($"Custom event value: {value}");
        }
    });
```

### Step 6 — Update Interface Usage
| v1 interfaces | v2 interfaces |
|---------------|---------------|
| `ICpuThreadActivity`, `ICpuThreadActivity2` | `ICpuThreadActivity` |
| `IReadyThreadEvent`, `IReadyThreadEvent2` | `IReadyThreadEvent` |
| `IClassicStackEvent`, `IGenericStackEvent`, `ITraceMessageStackEvent` | `IStackEvent` |
| `IPathNode` | Path strings directly |

---

## 4. No-change Example
```csharp
// Works in both v1 and v2
using var traceProcessor = TraceProcessor.Create(@"trace.etl");

var cpuData = traceProcessor.UseContextSwitchData();
var processData = traceProcessor.UseProcesses();

traceProcessor.Process();

foreach (var cs in cpuData.Result)
{
    Console.WriteLine($"Process: {cs.Process?.ImageName}");
}
```

---

## 5. New in v2
- `UseBootTraceData()` — boot performance analysis  
- `UseAIFabricData()` — AI fabric workload analysis  
- `UseOnnxData()` — ONNX model execution analysis  
- Real-time trace processing via `IStreamingTraceSource`

---

## 6. Quick Migration Checklist
- [ ] Update NuGet package to v2  
- [ ] Change target framework to .NET Standard 2.1+  
- [ ] Replace `int` IDs with `uint`  
- [ ] Replace legacy timestamp types  
- [ ] Update any streaming event processing  
- [ ] Compile & test

---

## 7. Resources
- [EventTracing v2 NuGet](https://www.nuget.org/packages/Microsoft.Windows.EventTracing/)  
- [Microsoft Performance Toolkit Docs](https://docs.microsoft.com/en-us/windows/apps/trace-processing/)  
- [ETW Trace Processing Overview](https://docs.microsoft.com/en-us/windows/apps/trace-processing/overview)  
