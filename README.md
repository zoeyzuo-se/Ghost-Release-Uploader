# Ghost-Release-Uploader
Azure Function that will check the Ghost repository for new releases and compares it with the latest merged release in the Ghost-Azure repository. If newer versions of Ghost are released, the files will be downloaded and merged into the Ghost-Azure repository. 
## Installation
1. Fork the Ghost-Azure repository  
2. Deploy to the Ghost-Release-Uploader an Azure Function App  
3. Add the following Application Settings with their corresponding values  
  a. GitUserName  
  b. GitPassword  
  c. GitRepoOwner  
  d. GitRepoName  
  e. GitRepoBranch  
  f. GitAuthorName  
  g. GitAuthorEmail  
