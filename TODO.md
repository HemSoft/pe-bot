# TODO

## Confluence Integration

### Search Improvements
- [ ] Investigate and fix topic filtering in GetRecentUpdates to ensure results are actually related to the search topic
- [ ] Verify if date filtering is working correctly with both v1 and v2 APIs
- [ ] Consider adding relevance scoring/ranking to ensure returned results are actually about the search topic
- [ ] Consider extracting query building logic into a separate service for better maintainability
- [ ] Add configurable search options (exact match vs fuzzy, search in title/content/both)

### General Improvements
- [ ] Split ConfluenceInfoProvider into smaller classes (currently over 200 lines)
- [ ] Extract API version handling into a separate service
- [ ] Add retry handling for transient failures
- [ ] Add caching layer for frequently accessed pages
- [ ] Improve error messages with troubleshooting suggestions
- [ ] Add telemetry for search query performance and result quality
- [ ] Add integration tests to verify behavior against actual Confluence instance
- [ ] Add test cases for different date/time scenarios in FormatRelativeTime
- [ ] Improve URL encoding handling for special characters in search queries

- [ ] Color coding of console output. Create a Console helper class for this.

### Before going to production:
- [ ] Do not use my fhemmer@relias.com account for authenticating to the Assistant.
- [ ] Run this in Docker.
- [ ] Create deployment pipelines and bicep files.

# DONE

- [x] Added support for both v1 and v2 Confluence Cloud APIs
- [x] Implemented proper authentication with Basic Auth
- [x] Added detailed logging for API responses
- [x] Added proper error handling for API failures
- [x] Implemented relative time formatting for page updates
- [x] Added unit tests for Confluence query construction to validate search behavior
- [x] Added comprehensive test coverage for search functionality
- [x] Implemented proper URL encoding for API requests
- [x] Added mock testing infrastructure for API responses
- [x] Removed the AI requirement at the beginning of a sentence
- [x] Implement all Confluence callback functions