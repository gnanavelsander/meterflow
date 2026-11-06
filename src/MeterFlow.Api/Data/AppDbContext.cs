using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using MeterFlow.Api.Projects;
using MeterFlow.Api.Datasets;
namespace MeterFlow.Api.Data;
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext<IdentityUser>(options)
{
    public DbSet<EnergyProject> Projects => Set<EnergyProject>();
    public DbSet<EnergyDataset> Datasets => Set<EnergyDataset>();
    public DbSet<Measurement> Measurements => Set<Measurement>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        base.OnModelCreating(model);
        model.Entity<EnergyDataset>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.FileName).HasMaxLength(255);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.HasOne(x => x.Project).WithMany().HasForeignKey(x => x.ProjectId);
            e.HasIndex(x => new { x.ProjectId, x.UploadedAt });
        });
        model.Entity<Measurement>(e =>
        {
            e.HasKey(x => new { x.DatasetId, x.BuildingId, x.Timestamp });
            e.Property(x => x.BuildingId).HasMaxLength(64);
            e.Property(x => x.ConsumptionKwh).HasPrecision(18, 6);
            e.HasOne(x => x.Dataset).WithMany().HasForeignKey(x => x.DatasetId);
        });
        model.Entity<EnergyProject>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.HasOne<IdentityUser>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.OwnerId, x.CreatedAt });
        });
    }
}
