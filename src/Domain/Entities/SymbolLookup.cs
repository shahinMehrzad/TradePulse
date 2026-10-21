namespace Domain.Entities
{
    /// <summary>Reference row for <c>SymbolEnum</c>. <see cref="Id"/> is the enum's numeric value; rows are only ever created from the enum.</summary>
    public class SymbolLookup
    {
        public int Id { get; set; }
        public required string Name { get; set; }
        public string? Description { get; set; }
    }
}
