function tryLock(lockId, createdDate, ttl) {
    var collection = getContext().getCollection();
    var collectionLink = collection.getSelfLink();
    var response = getContext().getResponse();

    var query = 'select * from root r where r.id = "' + lockId + '"';
    collection.queryDocuments(collectionLink, query, {}, function(err, documents) {
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
        let lockItem = {id: lockId, createdDate: createdDate, ttl: ttl};
        collection.createDocument(collectionLink, lockItem,
            function (err, createdDocument) {
                if (err) {
                    throw err;
                }
                response.setBody({locked: true, etag: createdDocument._etag, createdDate: createdDocument.createdDate});
            });
    }
}