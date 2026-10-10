using Microsoft.Data.SqlClient;
using Privacy.TestSupport;
using Shouldly;
using Xunit;

namespace Privacy.Api.Tests;

/// <summary>The index behind "one request carries a subject's id at a time" (ADR-092), against a real engine.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class ErasureRequestIndexTests(ServiceFixture fixture) : IAsyncLifetime
{
    private const string InsertSql =
        "INSERT INTO privacy.ErasureRequests (RequestId, SubjectId, Status, RaisedAt, DueAt, RespondersCsv) " +
        "VALUES ({0}, {1}, 'Open', SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET(), 'ordering')";

    private const string InsertWithoutSubjectSql =
        "INSERT INTO privacy.ErasureRequests (RequestId, SubjectId, Status, RaisedAt, DueAt, RespondersCsv) " +
        "VALUES ({0}, NULL, 'Open', SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET(), 'ordering')";

    public ValueTask InitializeAsync() => new(fixture.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_second_request_carrying_the_same_subject_is_refused_by_the_engine()
    {
        Guid subject = Guid.CreateVersion7();
        await fixture.ExecuteAsync(InsertSql, Guid.CreateVersion7(), subject);

        Exception refused = await Should.ThrowAsync<Exception>(
            () => fixture.ExecuteAsync(InsertSql, Guid.CreateVersion7(), subject));

        // 2601 is a unique index, 2627 a constraint; either way it is the subject's uniqueness that held.
        (refused.GetBaseException() as SqlException)?.Number.ShouldBeOneOf(2601, 2627);
    }

    [Fact]
    public async Task Requests_that_have_handed_their_subject_over_to_the_hash_may_be_many()
    {
        await fixture.ExecuteAsync(InsertWithoutSubjectSql, Guid.CreateVersion7());
        await fixture.ExecuteAsync(InsertWithoutSubjectSql, Guid.CreateVersion7());

        (await fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM privacy.ErasureRequests")).ShouldBe(2);
    }

    [Fact]
    public async Task A_subject_may_be_raised_again_once_the_earlier_request_has_closed()
    {
        Guid subject = Guid.CreateVersion7();
        Guid first = Guid.CreateVersion7();
        await fixture.ExecuteAsync(InsertSql, first, subject);
        await fixture.ExecuteAsync("UPDATE privacy.ErasureRequests SET SubjectId = NULL WHERE RequestId = {0}", first);

        await fixture.ExecuteAsync(InsertSql, Guid.CreateVersion7(), subject);

        (await fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM privacy.ErasureRequests")).ShouldBe(2);
    }
}
