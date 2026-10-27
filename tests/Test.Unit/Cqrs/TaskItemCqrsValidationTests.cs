using EF.Common.Contracts;
using EF.CQRS.Abstractions;
using EF.CQRS.Decorators;
using EF.CQRS.Validation;
using EF.Domain.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TaskFlow.Application.Cqrs.Features.TaskItems;
using TaskFlow.Application.Models;
using Test.Support;

namespace Test.Unit.Cqrs;

/// <summary>Covers task item CQRS validation behavior with focused assertions that document expected behavior and regression intent.</summary>
[TestClass]
public sealed class TaskItemCqrsValidationTests
{
    /// <summary>Verifies that given invalid task item create, when validation decorator runs, then handler is not called.</summary>
    [TestMethod]
    public async Task Given_InvalidTaskItemCreate_When_ValidationDecoratorRuns_Then_HandlerIsNotCalled()
    {
        var tenantId = TestConstants.TenantId;
        var requestContext = new Mock<IRequestContext<string, Guid?>>();
        requestContext.SetupGet(x => x.TenantId).Returns(tenantId);
        requestContext.SetupGet(x => x.Roles).Returns(new List<string>());

        var inner = new TrackingHandler();
        var validator = new CreateTaskItemCommandValidator(requestContext.Object);
        var decorator = new ValidationRequestHandlerDecorator<CreateTaskItemCommand, Result<DefaultResponse<TaskItemDto>>>(
            inner,
            [validator],
            new FailableValidationResponseFactory<Result<DefaultResponse<TaskItemDto>>>(),
            NullLogger<ValidationRequestHandlerDecorator<CreateTaskItemCommand, Result<DefaultResponse<TaskItemDto>>>>.Instance);

        var command = new CreateTaskItemCommand(new DefaultRequest<TaskItemDto>
        {
            Item = new TaskItemDto { Title = "" }
        });

        var result = await decorator.HandleAsync(command, TestContext.CancellationToken);

        Assert.IsFalse(result.IsSuccess);
        Assert.IsFalse(inner.WasCalled);
        Assert.AreEqual(tenantId, command.Request.Item.TenantId);
        Assert.Contains("Title is required", result.ToString());
    }

    /// <summary>Supports test execution for Test.unit CQRS scenarios.</summary>
    private sealed class TrackingHandler
        : IRequestHandler<CreateTaskItemCommand, Result<DefaultResponse<TaskItemDto>>>
    {
        public bool WasCalled { get; private set; }

        /// <summary>Verifies handle behavior and protects the expected test contract.</summary>
        public Task<Result<DefaultResponse<TaskItemDto>>> HandleAsync(
            CreateTaskItemCommand request,
            CancellationToken ct = default)
        {
            WasCalled = true;
            return Task.FromResult(new Result<DefaultResponse<TaskItemDto>>(new DefaultResponse<TaskItemDto>
            {
                Item = request.Request.Item
            }));
        }
    }

    public TestContext TestContext { get; set; } = null!;
}
