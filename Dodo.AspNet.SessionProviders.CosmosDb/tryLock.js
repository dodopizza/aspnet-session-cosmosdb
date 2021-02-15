function tryLock(sessionId, createdDate, ttl) {
    var collection = getContext().getCollection();
    var collectionLink = collection.getSelfLink();
    var response = getContext().getResponse();

    var query = 'select * from root r where r.id = "' + sessionId + '"';
    collection.queryDocuments(collectionLink, query, {}, function(err, documents, responseOptions) {
        if (err)
        {
            throw err;
        }
        if (documents.length > 0)
        {
            var existingDocument = documents[0];
            var responseDoc = {locked: false, etag: existingDocument._etag, createdDate: existingDocument.createdDate };
            response.setBody(responseDoc);
        }
        else
        {
            createNewLockRecord();
        }
    });

    function createNewLockRecord() {
        var lockItem = {id: sessionId, createdDate: createdDate, ttl: ttl}; // createdDate
        collection.createDocument(collectionLink, lockItem,
            function (err, createdDocument) {
                if (err) {
                    throw err;
                }
                response.setBody({locked: true, etag: createdDocument._etag, createdDate: createdDocument.createdDate});
            });
    }
}