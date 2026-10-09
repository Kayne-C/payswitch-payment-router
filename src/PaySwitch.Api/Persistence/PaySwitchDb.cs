using Microsoft.EntityFrameworkCore;
using PaySwitch.Api.Domain;

namespace PaySwitch.Api.Persistence;

public sealed class PaySwitchDb(DbContextOptions<PaySwitchDb> options) : DbContext(options)
{
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<ProviderAttempt> Attempts => Set<ProviderAttempt>();
    public DbSet<Compensation> Compensations => Set<Compensation>();
    public DbSet<LedgerEntry> Ledger => Set<LedgerEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Payment>(e =>
        {
            e.ToTable("payments");
            e.HasKey(p => p.Id);
            e.Property(p => p.MerchantId).HasMaxLength(64);
            e.Property(p => p.IdempotencyKey).HasMaxLength(128);
            e.Property(p => p.RequestHash).HasMaxLength(64);
            e.Property(p => p.Currency).HasMaxLength(3);
            e.Property(p => p.PaymentMethodToken).HasMaxLength(128);
            e.Property(p => p.State).HasConversion<string>().HasMaxLength(16);
            e.Property(p => p.FailureReason).HasConversion<string>().HasMaxLength(24);

            e.HasIndex(p => new { p.MerchantId, p.IdempotencyKey }).IsUnique();
            e.HasIndex(p => new { p.State, p.NextAttemptAt });
            e.Ignore(p => p.IsTerminal);
        });

        b.Entity<ProviderAttempt>(e =>
        {
            e.ToTable("provider_attempts");
            e.HasKey(a => a.Id);
            e.Property(a => a.Outcome).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(a => a.PaymentId);
        });

        b.Entity<Compensation>(e =>
        {
            e.ToTable("compensations");
            e.HasKey(c => c.Id);
            e.Property(c => c.Status).HasConversion<string>().HasMaxLength(8);
            e.HasIndex(c => new { c.Status, c.NextAttemptAt });

            e.HasIndex(c => new { c.PaymentId, c.Provider }).IsUnique();
        });

        b.Entity<LedgerEntry>(e =>
        {
            e.ToTable("ledger_entries");
            e.HasKey(l => l.Id);

            e.HasIndex(l => l.PaymentId).IsUnique();
        });
    }
}
