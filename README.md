# Dynamics Account Processor

## Overview

The **Dynamics Account Processor** is an Azure Function-based application designed to retrieve account data from a Dynamics 365 instance, process it, and then export the data in CSV format to Azure Blob Storage. This solution leverages Microsoft Identity to authenticate via Azure AD, processes the data in batches, applies retry logic for resiliency, and asynchronously handles the export of processed data.

The functionality is designed to operate in high-volume environments with the ability to scale and manage concurrent processing efficiently. This can be especially useful in business scenarios where large volumes of account data need to be processed and archived.

## Key Features

- **Authenticate** to Microsoft Dynamics 365 using Azure AD credentials (Client ID, Client Secret, and Tenant ID).
- **Batch Processing** of account IDs from Dynamics 365 in parallel with retry mechanisms using the Polly library.
- **CSV Export** of processed data to Azure Blob Storage.
- **Retry Logic** with exponential backoff for handling transient errors (e.g., 429 Too Many Requests, or service unavailability).
- **Concurrency Management** with `SemaphoreSlim` to limit the number of parallel batch requests, optimizing the system for reliability and performance.

## Prerequisites

To use this application, you must have the following:

1. **Azure Subscription**: Required for hosting the function and storing data in Azure Blob Storage.
2. **Microsoft Dynamics 365 instance**: Access to a Dynamics 365 account to fetch account data via its API.
3. **Azure Active Directory (AAD) App Registration**:
   - Client ID
   - Client Secret
   - Tenant ID
4. **Azure Blob Storage**: A container to store the exported CSV files.
5. **Local Development Setup** (if running locally):
   - **Azure Functions Core Tools** (for local development and testing).
   - **Azurite** (if using local emulation for Azure Blob Storage).

## Getting Started

### 1. Clone the Repository

Clone this repository to your local machine:

```bash
git clone https://github.com/username/DynamicsAccountProcessor.git
cd DynamicsAccountProcessor
```

### 2. Install Dependencies

Make sure you have the required dependencies installed:

- **.NET SDK** (compatible v8 ideally for this project)
- **Azure Functions Tools** (for local development)

```bash
dotnet restore
```

### 3. Configure Environment Variables

Set the following environment variables in your local development environment or Azure Functions configuration:

- `CLIENT_ID`: Your Azure AD application client ID.
- `CLIENT_SECRET`: Your Azure AD application client secret.
- `TENANT_ID`: Your Azure AD tenant ID.
- `DYNAMICS_URL`: The base URL for your Dynamics 365 instance (e.g., `https://yourorganization.crm.dynamics.com`).
- `BLOB_CONNECTION_STRING`: The connection string to your Azure Blob Storage account.
- `BLOB_CONTAINER_NAME`: The name of the blob container where the CSV file will be saved.

Example of local development settings in `local.settings.json`:

```json
{
  "IsEncrypted": false,
  "Values": {
    "CLIENT_ID": "your-client-id",
    "CLIENT_SECRET": "your-client-secret",
    "TENANT_ID": "your-tenant-id",
    "DYNAMICS_URL": "https://yourorganization.crm.dynamics.com",
    "BLOB_CONNECTION_STRING": "DefaultEndpointsProtocol=https;AccountName=youraccount;AccountKey=yourkey;BlobEndpoint=yourendpoint",
    "BLOB_CONTAINER_NAME": "yourcontainername",
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet"
  }
}
```

### 4. Run the Function Locally

To run the function locally, use the following command:

```bash
func start
```

This will start the Azure Function app on your local machine. The function can be triggered via an HTTP request, either a `GET` or `POST` request to the endpoint `/api/ProcessAccountsHttp`.

### 5. Deploy to Azure

1. Create a Function App on Azure via the Azure portal.
2. Deploy your function code to Azure using Visual Studio or the Azure CLI.

## Function Flow

### 1. **Authentication and Access Token Retrieval**
   The function retrieves an access token from Azure AD using the `ConfidentialClientApplication` API. This token is used for authenticating requests to the Dynamics 365 API.

### 2. **Fetching Account IDs**
   It sends a `GET` request to the Dynamics 365 API to fetch all accounts with the field `cr356_processed` set to `false`, which indicates accounts that have not been processed yet.

### 3. **Batch Processing of Account Data**
   The account IDs are divided into batches of `BatchSize` (default: 100). These batches are processed concurrently with a maximum number of concurrent batches (`MaxConcurrentBatches`, default: 5). For each batch, a retry policy is applied using **Polly** in case of network failures or rate-limiting issues.

### 4. **Processing Account Data**
   For each account in the batch, the function retrieves detailed information (e.g., name, telephone, address, revenue, etc.) and updates the `cr356_processed` field to `TRUE`.

### 5. **Exporting Data to Azure Blob Storage**
   After processing all accounts, the function exports the resulting data to a CSV file and uploads it to an Azure Blob Storage container. The file is named with the current date and time to ensure uniqueness.

### 6. **Response**
   The function returns an HTTP response indicating whether the operation was successful or if an error occurred during the processing.

## Code Structure

### 1. **ProcessAccountsFunction**
   This is the main Azure Function that is triggered by HTTP requests. It handles the following:

   - Authentication with Azure AD to get the access token.
   - Fetching and processing account data in batches.
   - Uploading the processed data to Azure Blob Storage.

### 2. **AccountRecord**
   This class defines the structure of the data being processed and exported. It uses **CsvHelper** annotations for mapping the class properties to CSV columns.

### 3. **EnumerableExtensions**
   A helper class to extend `IEnumerable` with a `Batch` method for dividing the list into smaller chunks.

### 4. **Error Handling & Logging**
   Comprehensive error handling is implemented using **Polly** for retry logic and **ILogger** for detailed logs.

## Deployment

### 1. **Set Up Function App in Azure Portal**
   - Create a new Function App.
   - Configure the necessary environment variables (e.g., `CLIENT_ID`, `CLIENT_SECRET`, `BLOB_CONNECTION_STRING`).
   - Set up Azure Blob Storage for the output.

### 2. **Deploy Using Azure CLI**

```bash
az functionapp deployment source config-zip \
  --resource-group <your-resource-group> \
  --name <your-function-app-name> \
  --src <path-to-zip-file>
```

### 3. **Monitor & Debug**
   - Use the Azure portal's **Application Insights** feature for monitoring function invocations and errors.
   - Ensure the proper logging is enabled to track processing progress and any issues encountered during execution.

## Contributing

Contributions are welcome! If you'd like to improve or extend the functionality of this project, feel free to submit a pull request or open an issue. Please follow the guidelines below:

1. Fork the repository.
2. Clone your fork locally and create a new branch.
3. Make your changes.
4. Commit your changes and push them back to your fork.
5. Submit a pull request.

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

---

## Contact

- **GitHub**: [https://github.com/zahidprvz](https://github.com/zahidprvz)
- **LinkedIn**: [https://www.linkedin.com/in/zahidprvz/](https://www.linkedin.com/in/zahidprvz/)
- **Email**: [pervaizzahid55@gmail.com](mailto:pervaizzahid55@gmail.com)
