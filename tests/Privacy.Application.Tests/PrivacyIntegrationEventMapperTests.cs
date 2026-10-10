using Common.Application;
using Common.Contracts.Privacy.V1;
using Common.Domain;
using Microsoft.Extensions.DependencyInjection;
using Privacy.Domain.ErasureRequests.Events;
using Shouldly;
using Xunit;

namespace Privacy.Application.Tests;

public class PrivacyIntegrationEventMapperTests
{
    private static readonly DateTimeOffset Raised = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Request = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Subject = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static IIntegrationEventMapper Mapper()
    {
        ServiceCollection services = new();
        services.AddPrivacyApplication();
        return services
            .BuildServiceProvider()
            .CreateScope()
            .ServiceProvider
            .GetRequiredService<IIntegrationEventMapper>();
    }

    [Fact]
    public void A_raised_request_becomes_PersonalDataDeleteRequested_correlated_on_the_request()
    {
        PersonalDataDeleteRequested contract = Mapper()
            .Map([new ErasureRequestedDomainEvent(Request, Subject, Raised)])
            .ShouldHaveSingleItem()
            .ShouldBeOfType<PersonalDataDeleteRequested>();

        contract.RequestId.ShouldBe(Request);
        contract.SubjectId.ShouldBe(Subject);
        contract.CorrelationId.ShouldBe(Request);
        contract.OccurredAt.ShouldBe(Raised);
        contract.MessageId.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public void Each_publication_carries_a_message_id_of_its_own()
    {
        IIntegrationEventMapper mapper = Mapper();
        ErasureRequestedDomainEvent raised = new(Request, Subject, Raised);

        PersonalDataDeleteRequested first = (PersonalDataDeleteRequested)mapper.Map([raised]).Single();
        PersonalDataDeleteRequested second = (PersonalDataDeleteRequested)mapper.Map([raised]).Single();

        second.MessageId.ShouldNotBe(first.MessageId, "a reissue is the same request under a fresh message (ADR-092)");
    }

    [Fact]
    public void An_event_outside_the_allow_list_stays_local()
    {
        Mapper().Map([new UnmappedEvent(Raised)]).ShouldBeEmpty();
    }

    private sealed record UnmappedEvent(DateTimeOffset OccurredAt) : IDomainEvent;
}
