using System.Text;
using Microsoft.EntityFrameworkCore;

namespace ArrControl.Infrastructure.Persistence;

internal static class SnakeCaseModelBuilderExtensions
{
    public static void UseSnakeCaseColumnNames(this ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.Name));
            }
        }
    }

    private static string ToSnakeCase(string value)
    {
        var result = new StringBuilder(value.Length + 8);

        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (index > 0 && char.IsUpper(current))
            {
                var previous = value[index - 1];
                var nextIsLower = index + 1 < value.Length && char.IsLower(value[index + 1]);
                if (char.IsLower(previous) || char.IsDigit(previous) || nextIsLower)
                {
                    result.Append('_');
                }
            }

            result.Append(char.ToLowerInvariant(current));
        }

        return result.ToString();
    }
}
