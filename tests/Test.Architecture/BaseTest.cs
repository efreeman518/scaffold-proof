using EF.CQRS.Abstractions;
using System.Reflection;
using TaskFlow.Application.Contracts;
using TaskFlow.Application.Cqrs.Registration;
using TaskFlow.Domain.Model;
using TaskFlow.Infrastructure.Data;
using TaskFlow.Infrastructure.Repositories;

namespace Test.Architecture;

/// <summary>
/// Abstract base for the architecture-rule test classes; resolves and caches the assemblies under test
/// (Domain.Model, Application.Contracts, Application.Services, Application.Cqrs, Infrastructure.Data,
/// Infrastructure.Repositories) so each rule fixture can target them by reference.
/// Not a test class - pure-unit tier helper.
/// </summary>
public abstract class BaseTest
{
    protected static readonly Assembly DomainModelAssembly = typeof(Category).Assembly;
    protected static readonly Assembly ApplicationContractsAssembly = typeof(AppConstants).Assembly;
    protected static readonly Assembly ApplicationServicesAssembly = Assembly.Load("TaskFlow.Application.Services");
    protected static readonly Assembly ApplicationCqrsAssembly = typeof(CqrsApplicationRegistration).Assembly;
    protected static readonly Assembly EfCqrsAssembly = typeof(IRequestHandler<,>).Assembly;
    protected static readonly Assembly InfrastructureDataAssembly = typeof(TaskFlowDbContextTrxn).Assembly;
    protected static readonly Assembly InfrastructureRepositoriesAssembly = typeof(CategoryRepositoryTrxn).Assembly;
    protected static readonly Assembly ApiAssembly = Assembly.Load("TaskFlow.Api");
}
