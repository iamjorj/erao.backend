namespace Erao.Core.Interfaces;

public interface IParquetConversionService
{
    Task<ParquetConversionResult> ConvertCsvToParquetAsync(Stream csvStream, string outputPath);
    Task<ParquetConversionResult> ConvertExcelToParquetAsync(Stream excelStream, string outputPath);
    Task<string> GetParquetSchemaAsync(string parquetPath);
    Task<string> GetSampleDataAsync(string parquetPath, int count = 100);
    Task DeleteParquetFileAsync(string parquetPath);
}

public class ParquetConversionResult
{
    public bool Success { get; set; }
    public string? ParquetPath { get; set; }
    public long RowCount { get; set; }
    public string? SchemaInfoJson { get; set; }
    public string? SampleDataJson { get; set; }
    public string? ErrorMessage { get; set; }
}
