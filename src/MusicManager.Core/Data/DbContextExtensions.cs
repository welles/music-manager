using Microsoft.EntityFrameworkCore;

namespace MusicManager.Core.Data;

public static class DbContextExtensions
{
    /// <summary>
    /// Saves the changes of a long-running job that updates rows others may delete in the meantime (the library
    /// index removes an entry when its file is moved, replaced or deleted): a row that is gone is dropped from
    /// the save instead of failing all of it. Returns the number of dropped rows.
    /// </summary>
    public static async Task<int> SaveSkippingDeletedAsync(this DbContext db, CancellationToken ct)
    {
        var dropped = 0;
        while (true)
        {
            try
            {
                await db.SaveChangesAsync(ct);
                return dropped;
            }
            catch (DbUpdateConcurrencyException ex) when (ex.Entries.Count > 0)
            {
                foreach (var entry in ex.Entries) entry.State = EntityState.Detached;
                dropped += ex.Entries.Count;
            }
        }
    }
}
