using Common.Application;
using Microsoft.Extensions.DependencyInjection;

namespace Notifications.Infrastructure.Delivery;

/// <summary>The transaction both senders commit a claimed row's move in.</summary>
internal static class RowCommit
{
    /// <summary>
    /// <paramref name="move"/> over the row <paramref name="load"/> finds, saved in one transaction; false when the
    /// row is gone or the move changed nothing.
    /// </summary>
    public static async Task<bool> ExecuteAsync<TRow>(
        IServiceProvider sp,
        Func<CancellationToken, Task<TRow?>> load,
        Func<TRow, DateTimeOffset, CancellationToken, Task<bool>> move,
        CancellationToken ct)
        where TRow : class
    {
        IUnitOfWork unitOfWork = sp.GetRequiredService<IUnitOfWork>();

        return await unitOfWork.ExecuteAsync(
            async inner =>
            {
                TRow? row = await load(inner);

                if (row is null)
                    return false;

                bool moved = await move(row, sp.GetRequiredService<TimeProvider>().GetUtcNow(), inner);
                await unitOfWork.SaveChangesAsync(inner);

                return moved;
            },
            ct);
    }
}
