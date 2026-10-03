namespace Domain.ValueObjects
{
    public readonly record struct Price
    {
        public decimal Value { get; }

        public Price(decimal value)
        {
            if (value < 0)
                throw new ArgumentException("Price cannot be negative.", nameof(value));

            Value = value;
        }

        public static implicit operator decimal(Price price) => price.Value;
        public static explicit operator Price(decimal value) => new(value);
    }
}
