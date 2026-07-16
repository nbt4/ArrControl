using ArrControl.Infrastructure.Persistence.Connections;
using Microsoft.EntityFrameworkCore;

namespace ArrControl.Infrastructure.Persistence;

public sealed class ArrControlDbContext(DbContextOptions<ArrControlDbContext> options) : DbContext(options)
{
    public DbSet<InstanceEntity> Instances => Set<InstanceEntity>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(ArrControlDbContext).Assembly);
        builder.UseSnakeCaseColumnNames();
    }
}
