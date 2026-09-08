@description('Resource name prefix')
param resourcePrefix string

@description('Azure region')
param location string

@description('Tags')
param tags object = {}

@description('Search backend (D-040); the embedding subscription exists only for PgVector')
@allowed([
  'AzureAiSearch'
  'PgVector'
  'Sql'
])
param searchProvider string = 'AzureAiSearch'

resource serviceBusNamespace 'Microsoft.ServiceBus/namespaces@2024-01-01' = {
  name: '${resourcePrefix}-sb-${uniqueString(resourceGroup().id)}'
  location: location
  tags: tags
  sku: {
    name: 'Standard'
    tier: 'Standard'
  }
  properties: {
    minimumTlsVersion: '1.2'
    disableLocalAuth: true // Managed identity only
  }
}

resource domainEventsTopic 'Microsoft.ServiceBus/namespaces/topics@2024-01-01' = {
  parent: serviceBusNamespace
  name: 'DomainEvents'
  properties: {
    maxSizeInMegabytes: 1024
    defaultMessageTimeToLive: 'P14D'
    requiresDuplicateDetection: true
    duplicateDetectionHistoryTimeWindow: 'PT1H'
  }
}

// Three per-consumer subscriptions replace the single function-processor subscription, each filtered to the
// EventType application property set by the envelope. Creating a named SQL filter rule below removes the
// subscription's implicit $Default rule (match-all), so only messages matching the named rule are delivered.
resource projectionSubscription 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2024-01-01' = {
  parent: domainEventsTopic
  name: 'projection'
  properties: {
    maxDeliveryCount: 5
    lockDuration: 'PT5M'
    deadLetteringOnMessageExpiration: true
    deadLetteringOnFilterEvaluationExceptions: true
  }
}

resource projectionRule 'Microsoft.ServiceBus/namespaces/topics/subscriptions/rules@2024-01-01' = {
  parent: projectionSubscription
  name: 'EventTypeFilter'
  properties: {
    filterType: 'SqlFilter'
    sqlFilter: {
      sqlExpression: 'EventType IN (\'TaskItemCreatedEvent\', \'TaskItemStatusChangedEvent\', \'TaskItemCompletedEvent\')'
    }
  }
}

resource aiReviewSubscription 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2024-01-01' = {
  parent: domainEventsTopic
  name: 'ai-review'
  properties: {
    maxDeliveryCount: 5
    lockDuration: 'PT5M'
    deadLetteringOnMessageExpiration: true
    deadLetteringOnFilterEvaluationExceptions: true
  }
}

resource aiReviewRule 'Microsoft.ServiceBus/namespaces/topics/subscriptions/rules@2024-01-01' = {
  parent: aiReviewSubscription
  name: 'EventTypeFilter'
  properties: {
    filterType: 'SqlFilter'
    sqlFilter: {
      sqlExpression: 'EventType = \'TaskItemCreatedEvent\''
    }
  }
}

resource workflowSubscription 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2024-01-01' = {
  parent: domainEventsTopic
  name: 'workflow'
  properties: {
    maxDeliveryCount: 5
    lockDuration: 'PT5M'
    deadLetteringOnMessageExpiration: true
    deadLetteringOnFilterEvaluationExceptions: true
  }
}

resource workflowRule 'Microsoft.ServiceBus/namespaces/topics/subscriptions/rules@2024-01-01' = {
  parent: workflowSubscription
  name: 'EventTypeFilter'
  properties: {
    filterType: 'SqlFilter'
    sqlFilter: {
      sqlExpression: 'EventType = \'TaskItemCreatedEvent\''
    }
  }
}

// D-040: the embedding consumer runs only on the PgVector arm. A subscription without a consumer would
// accumulate every task event until its TTL, so it is created exactly when something drains it.
resource embeddingSubscription 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2024-01-01' = if (searchProvider == 'PgVector') {
  parent: domainEventsTopic
  name: 'embedding'
  properties: {
    maxDeliveryCount: 5
    lockDuration: 'PT5M'
    deadLetteringOnMessageExpiration: true
    deadLetteringOnFilterEvaluationExceptions: true
  }
}

resource embeddingRule 'Microsoft.ServiceBus/namespaces/topics/subscriptions/rules@2024-01-01' = if (searchProvider == 'PgVector') {
  parent: embeddingSubscription
  name: 'EventTypeFilter'
  properties: {
    filterType: 'SqlFilter'
    sqlFilter: {
      sqlExpression: 'EventType IN (\'TaskItemCreatedEvent\', \'TaskItemContentChangedEvent\')'
    }
  }
}

resource taskCommandsQueue 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = {
  parent: serviceBusNamespace
  name: 'TaskCommands'
  properties: {
    maxSizeInMegabytes: 1024
    defaultMessageTimeToLive: 'P14D'
    deadLetteringOnMessageExpiration: true
    lockDuration: 'PT1M'
    maxDeliveryCount: 10
  }
}

output namespaceName string = serviceBusNamespace.name
output namespaceEndpoint string = '${serviceBusNamespace.name}.servicebus.windows.net'
