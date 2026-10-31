namespace Domain.Entities
{
    public class SymbolLookup
    {
        public int Id { get; set; }
        public required string Name { get; set; }
        public string? Description { get; set; }
    }
}
