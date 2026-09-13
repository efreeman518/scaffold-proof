using 'main.bicep'

param resourcePrefix = 'taskflow'
param environmentName = 'dev'
param location = 'eastus2'
param searchProvider = 'Sql'

// Container images - updated by CI/CD workflow
param gatewayImage = 'mcr.microsoft.com/azuredocs/containerapps-helloworld:latest'
param apiImage = 'mcr.microsoft.com/azuredocs/containerapps-helloworld:latest'
param schedulerImage = 'mcr.microsoft.com/azuredocs/containerapps-helloworld:latest'
param blazorImage = 'mcr.microsoft.com/azuredocs/containerapps-helloworld:latest'
