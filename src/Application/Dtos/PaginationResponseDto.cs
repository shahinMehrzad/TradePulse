namespace Application.Dtos;

public sealed class PaginationResponseDto<T>
{
    public int TotalItemsCount { get; set; }
    public int TotalPages { get; set; }
    public int ItemsPerPage { get; set; }
    public int CurrentPage { get; set; }
    public IReadOnlyList<T>? Data { get; set; }
}
