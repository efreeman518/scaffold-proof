using TaskFlow.Application.Mappers;
using TaskFlow.Application.Models;
using TaskFlow.Domain.Shared;
using TaskFlow.Domain.Shared.Enums;
using Test.Support;
using Test.Support.Builders;

namespace Test.Unit.Mappers;

/// <summary>
/// Validates <c>TaskItemMapper</c> entity <-> DTO mapping including the date-range and recurrence-pattern
/// flatten/unflatten paths and failure surfacing for invalid DTO input.
/// Pure-unit tier: static mapping extensions only - no infra.
/// </summary>
[TestClass]
public class TaskItemMapperTests
{
    /// <summary>Verifies that given valid entity, when mapped to DTO, then all properties mapped.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void Given_ValidEntity_When_MappedToDto_Then_AllPropertiesMapped()
    {
        var entity = new TaskItemBuilder().WithPriority(Priority.High).Build();
        var dto = entity.ToDto();

        Assert.AreEqual(entity.Id, dto.Id);
        Assert.AreEqual(entity.Title, dto.Title);
        Assert.AreEqual(entity.Description, dto.Description);
        Assert.AreEqual(entity.Priority, dto.Priority);
        Assert.AreEqual(entity.Status, dto.Status);
        Assert.AreEqual(entity.CategoryId, dto.CategoryId);
        Assert.AreEqual(entity.ParentTaskItemId, dto.ParentTaskItemId);
    }

    /// <summary>Verifies that given entity with date range, when mapped to DTO, then dates flattened correctly.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void Given_EntityWithDateRange_When_MappedToDto_Then_DatesFlattenedCorrectly()
    {
        var entity = new TaskItemBuilder().Build();
        var start = StoredPrecision.Timestamp(DateTimeOffset.UtcNow);
        var due = start.AddDays(7);
        entity.UpdateDateRange(start, due);

        var dto = entity.ToDto();

        Assert.AreEqual(start, dto.StartDate);
        Assert.AreEqual(due, dto.DueDate);
    }

    /// <summary>Verifies that given valid DTO, when mapped to entity, then returns success domain result.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void Given_ValidDto_When_MappedToEntity_Then_ReturnsSuccessDomainResult()
    {
        var dto = new TaskItemDto { Title = "New Task", Priority = Priority.Medium };
        var result = dto.ToEntity(TestConstants.TenantId);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("New Task", result.Value!.Title);
        Assert.AreEqual(Priority.Medium, result.Value.Priority);
        Assert.AreEqual(TaskItemStatus.Open, result.Value.Status);
    }

    /// <summary>Verifies that given DTO with dates, when mapped to entity, then date range set.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void Given_DtoWithDates_When_MappedToEntity_Then_DateRangeSet()
    {
        var start = StoredPrecision.Timestamp(DateTimeOffset.UtcNow);
        var due = start.AddDays(5);
        var dto = new TaskItemDto { Title = "With Dates", StartDate = start, DueDate = due };
        var result = dto.ToEntity(TestConstants.TenantId);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(start, result.Value!.DateRange.StartDate);
        Assert.AreEqual(due, result.Value.DateRange.DueDate);
    }

    /// <summary>Verifies that given DTO with recurrence, when mapped to entity, then recurrence pattern set.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void Given_DtoWithRecurrence_When_MappedToEntity_Then_RecurrencePatternSet()
    {
        var dto = new TaskItemDto
        {
            Title = "Recurring Task",
            RecurrenceInterval = 1,
            RecurrenceFrequency = "Weekly",
            RecurrenceEndDate = DateTimeOffset.UtcNow.AddMonths(3)
        };
        var result = dto.ToEntity(TestConstants.TenantId);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsNotNull(result.Value!.RecurrencePattern);
        Assert.AreEqual(1, result.Value.RecurrencePattern!.Interval);
        Assert.AreEqual("Weekly", result.Value.RecurrencePattern.Frequency);
    }

    /// <summary>Verifies that given invalid DTO, when mapped to entity, then returns failure.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public void Given_InvalidDto_When_MappedToEntity_Then_ReturnsFailure()
    {
        var dto = new TaskItemDto { Title = "" };
        var result = dto.ToEntity(TestConstants.TenantId);

        Assert.IsTrue(result.IsFailure);
    }
}
