from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union

if TYPE_CHECKING:
    from .content_resource_response import ContentResourceResponse
    from .effective_schema import EffectiveSchema

@dataclass
class ContentItemResponse(AdditionalDataHolder, Parsable):
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The contentTypeId property
    content_type_id: Optional[str] = None
    # The filename property
    filename: Optional[str] = None
    # The hasFile property
    has_file: Optional[bool] = None
    # The isDraft property
    is_draft: Optional[bool] = None
    # The keywords property
    keywords: Optional[list[str]] = None
    # The resource property
    resource: Optional[ContentResourceResponse] = None
    # The revisionId property
    revision_id: Optional[str] = None
    # The revisionNumber property
    revision_number: Optional[int] = None
    # The schema property
    schema: Optional[EffectiveSchema] = None
    # The schemaRevisionId property
    schema_revision_id: Optional[str] = None

    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> ContentItemResponse:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: ContentItemResponse
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return ContentItemResponse()

    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .content_resource_response import ContentResourceResponse
        from .effective_schema import EffectiveSchema

        from .content_resource_response import ContentResourceResponse
        from .effective_schema import EffectiveSchema

        fields: dict[str, Callable[[Any], None]] = {
            "contentTypeId": lambda n : setattr(self, 'content_type_id', n.get_str_value()),
            "filename": lambda n : setattr(self, 'filename', n.get_str_value()),
            "hasFile": lambda n : setattr(self, 'has_file', n.get_bool_value()),
            "isDraft": lambda n : setattr(self, 'is_draft', n.get_bool_value()),
            "keywords": lambda n : setattr(self, 'keywords', n.get_collection_of_primitive_values(str)),
            "resource": lambda n : setattr(self, 'resource', n.get_object_value(ContentResourceResponse)),
            "revisionId": lambda n : setattr(self, 'revision_id', n.get_str_value()),
            "revisionNumber": lambda n : setattr(self, 'revision_number', n.get_int_value()),
            "schema": lambda n : setattr(self, 'schema', n.get_object_value(EffectiveSchema)),
            "schemaRevisionId": lambda n : setattr(self, 'schema_revision_id', n.get_str_value()),
        }
        return fields

    def serialize(self,writer: SerializationWriter) -> None:
        """
        Serializes information the current object
        param writer: Serialization writer to use to serialize this model
        Returns: None
        """
        if writer is None:
            raise TypeError("writer cannot be null.")
        writer.write_str_value("contentTypeId", self.content_type_id)
        writer.write_str_value("filename", self.filename)
        writer.write_bool_value("hasFile", self.has_file)
        writer.write_bool_value("isDraft", self.is_draft)
        writer.write_collection_of_primitive_values("keywords", self.keywords)
        writer.write_object_value("resource", self.resource)
        writer.write_str_value("revisionId", self.revision_id)
        writer.write_int_value("revisionNumber", self.revision_number)
        writer.write_object_value("schema", self.schema)
        writer.write_str_value("schemaRevisionId", self.schema_revision_id)
        writer.write_additional_data_value(self.additional_data)


