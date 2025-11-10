# TextToJSON - Powerful Text to JSON Converter

A flexible .NET library for converting structured text (like invoices, receipts, logs) into JSON using template-based parsing.

## Features

- **Template-based parsing** - Define placeholders with data types and transformations
- **Data type conversion** - Automatic conversion to numbers, dates, times, etc.
- **Array/table detection** - Automatically parse repeating rows into JSON arrays
- **Expression engine** - Support for functions and pipelines
- **Pipe operations** - Transform values using pipes (upper, lower, prefix, suffix, etc.)
- **No external dependencies** - Uses only built-in .NET libraries

## Installation

This is a standalone C# file. Simply include `Program.cs` in your project.

**Requirements:**
- .NET 6.0 or higher

## Basic Usage

```csharp
// Define your template
const string template = @"
{RetailerName:wordwithspace}
{InvoiceDateTime:datetime:dd-MM-yyyy H:mm}
{TotalAmount:number}
";

// Your input text
const string input = @"
ABC Retailer
15-09-2025 3:45
Total Amount 246
";

// Parse and convert
var tmpl = TemplateParser.ParseTemplate(template);
var root = TextToJsonConverter.Convert(input, tmpl);
Console.WriteLine(root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
```

## Template Syntax

### Basic Placeholders

```
{FieldName:datatype:format}
```

- **FieldName** - The JSON property name
- **datatype** - Optional: `word`, `wordwithspace`, `number`, `integer`, `datetime`, `date`, `time`
- **format** - Optional: Format string for datetime parsing (e.g., `dd-MM-yyyy H:mm`)

### Examples

```
{CustomerName:wordwithspace}          // Text with spaces
{InvoiceNumber:word}                   // Single word
{Amount:number}                        // Decimal number
{Quantity:integer}                     // Integer
{Date:date:dd/MM/yyyy}                // Date with custom format
{DateTime:datetime:dd-MM-yyyy H:mm}   // DateTime with custom format
```

### Array/Table Parsing

Use `[]` to indicate array fields. All placeholders with the same array base will be parsed as array items.

```
Item Rate Qty Total
{Items[].ItemName:word} {Items[].Rate:number} {Items[].Quantity:integer} {Items[].Total:number}
```

**Input:**
```
Item Rate Qty Total
Item1 34 4 136
Item2 55 2 110
```

**Output:**
```json
{
  "Items": [
    {
      "ItemName": "Item1",
      "Rate": 34,
      "Quantity": 4,
      "Total": 136
    },
    {
      "ItemName": "Item2",
      "Rate": 55,
      "Quantity": 2,
      "Total": 110
    }
  ]
}
```

## Pipe Operations

Transform values using pipes (`|`). Multiple pipes can be chained.

### Available Pipes

| Pipe | Description | Example |
|------|-------------|---------|
| `upper()` | Convert to uppercase | `{Name:word \| upper()}` |
| `lower()` | Convert to lowercase | `{Name:word \| lower()}` |
| `trim()` | Trim whitespace | `{Value:word \| trim()}` |
| `prefix('text')` | Add prefix | `{Address:wordwithspace \| prefix('Addr: ')}` |
| `suffix('text')` | Add suffix | `{Name:word \| suffix(' Inc.')}` |
| `replace('old', 'new')` | Replace text | `{Value:word \| replace('-', '/')}` |
| `default('value')` | Default if empty | `{Optional:word \| default('N/A')}` |
| `tonumber()` | Convert to number | `{Value:word \| tonumber()}` |
| `dateformat('fmt')` | Format date | `{Date:date \| dateformat('yyyy-MM-dd')}` |

### Pipe Examples

```
{CustomerName:wordwithspace | upper()}
{Address:wordwithspace | prefix('Address: ')}
{Status:word | default('Pending') | upper()}
{Price:number | suffix(' USD')}
```

**Chaining pipes:**
```
{Name:wordwithspace | trim() | upper() | prefix('Customer: ')}
```

## Functions

Functions can be used in placeholders to compute values.

### Available Functions

| Function | Description | Example |
|----------|-------------|---------|
| `coalesce(a, b, ...)` | Return first non-empty value | `{coalesce(CustomerName, RetailerName)}` |
| `concat(a, b, ...)` | Concatenate values | `{concat(FirstName, " ", LastName)}` |
| `sum(array)` | Sum array values | `{sum(Items[].Total)}` |
| `count(array)` | Count array items | `{count(Items[])}` |
| `join(array, sep)` | Join array with separator | `{join(Items[].Name, ", ")}` |
| `valueof(path)` | Get value at path | `{valueof(CustomerName)}` |

### Function Examples

```
{coalesce(CustomerName, "Unknown"):wordwithspace}
{sum(Items[].Total):number}
{count(Items[]):integer}
{coalesce(TotalItem, RetailerName) | upper():wordwithspace}
```

## Complete Example

```csharp
const string template = @"
{RetailerName:wordwithspace}
{InvoiceDateTime:datetime:dd-MM-yyyy H:mm}
{Address:wordwithspace | prefix('Address: ')}
{BillNumber:wordwithspace}
Item Rate Qty Total
{Items[].ItemName:word} {Items[].Rate:number} {Items[].Quantity:integer} {Items[].Total:number}
Total Amount {TotalAmount:number}
Total Item {coalesce(TotalItem, RetailerName) | upper():wordwithspace}
";

const string input = @"
ABC Retailer
15-09-2025 3:45
NY,Pal Road, ZN
Bill Num 20084
Item Rate Qty Total
Item1 34 4 136
Item2 55 2 110
Total Amount 246
Total Item 2
Thank You
";

var tmpl = TemplateParser.ParseTemplate(template);
var root = TextToJsonConverter.Convert(input, tmpl);

var options = new JsonSerializerOptions { WriteIndented = true };
Console.WriteLine(root.ToJsonString(options));
```

**Output:**
```json
{
  "RetailerName": "ABC Retailer",
  "InvoiceDateTime": "2025-09-15T03:45:00.0000000",
  "Address": "Address: NY,Pal Road, ZN",
  "BillNumber": "Bill Num 20084",
  "Items": [
    {
      "ItemName": "Item1",
      "Rate": 34,
      "Quantity": 4,
      "Total": 136
    },
    {
      "ItemName": "Item2",
      "Rate": 55,
      "Quantity": 2,
      "Total": 110
    }
  ],
  "TotalAmount": 246,
  "TotalItem": "2"
}
```

## Data Types

| Type | Description | Example Input |
|------|-------------|---------------|
| `word` | Single word (no spaces) | `Invoice123` |
| `wordwithspace` | Text with spaces | `ABC Retailer` |
| `number` / `decimal` | Decimal number | `123.45` |
| `integer` / `int` | Integer number | `42` |
| `datetime` | Date and time | `15-09-2025 3:45` |
| `date` | Date only | `15-09-2025` |
| `time` | Time only | `3:45 PM` |

## Advanced Features

### Nested Objects

Use dot notation for nested JSON objects:

```
{Customer.Name:wordwithspace}
{Customer.Address.City:wordwithspace}
```

**Output:**
```json
{
  "Customer": {
    "Name": "John Doe",
    "Address": {
      "City": "New York"
    }
  }
}
```

### Custom Date Formats

Specify format after the data type:

```
{Date:date:dd/MM/yyyy}
{DateTime:datetime:yyyy-MM-dd HH:mm:ss}
{Time:time:HH:mm}
```

### Special Characters in Pipes

You can include special characters in pipe arguments:

```
{Address:wordwithspace | prefix('Address:- ')}
{Phone:word | prefix('+1-')}
{Amount:number | suffix(' USD')}
```

## Tips & Best Practices

1. **Array headers** - The parser will automatically skip header lines that match the template literal text
2. **Type inference** - If you don't specify a type, values will be treated as strings
3. **Whitespace** - The parser is flexible with whitespace in both template and input
4. **Regex capture** - For complex patterns, the parser builds capture regexes automatically
5. **Pipeline order** - Pipes are applied left to right, so order matters

## License

This project is provided as-is for educational and commercial use.

## Contributing

Feel free to extend this library with additional pipes, functions, or data types as needed for your use case.
